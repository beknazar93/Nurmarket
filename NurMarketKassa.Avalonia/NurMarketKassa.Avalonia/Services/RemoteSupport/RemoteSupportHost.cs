#if !NURANDROID
using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services.RemoteSupport;

/// <summary>2026-10-07, владелец: своя удалённая помощь для тех поддержки (решение 29.09 — «полностью своё, свой VPS»;
/// 07.10 — сервер на нашем VPS, оператор — отдельная программа). 2026-10-16: «Нур Поддержка» — единственный способ удалённой
/// помощи, сторонняя программа из кассы убрана. Касса-хост по протоколу NurRemoteSupport/PROTOCOL.md v1:
/// hello → код и PIN → запрос оператора → согласие кассира → поток экрана (изменившиеся куски JPEG, не больше 2 неподтверждённых
/// кадров) и ввод оператора (мышь, клавиши, текст) через SendInput. Логика потока — из проверенного FakeHost
/// (NurRemoteSupport/Tools/FakeHost). Только основной монитор. Никакого доступа без кнопки кассира «Разрешить».</summary>
public sealed class RemoteSupportHost : IAsyncDisposable
{
    public enum State { Connecting, WaitingOperator, AskingConsent, InSession, Ended }

    /// <summary>2026-10-15: ответ кассира. «Разрешить» даёт экран и управление; «Только просмотр» — экран без ввода
    /// (касса сама не пропускает мышь и клавиатуру, что бы ни прислал сервер).</summary>
    public enum Consent { Deny, Allow, ViewOnly }

    /// <summary>Адрес ретранслятора (wss://…/ws/host). 2026-10-16: по умолчанию — боевой <see cref="DefaultRelayUrl"/>, своя
    /// поддержка включена всегда. Для проверки его заменяет переменная NURREMOTE_RELAY или файл
    /// %LOCALAPPDATA%\NurMarketKassa\remote-relay.txt (ws:// — только для сервера на этом ПК, см. RunAsync); пустые — боевой адрес.</summary>
    public static string RelayUrl
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("NURREMOTE_RELAY");
            if (!string.IsNullOrWhiteSpace(env))
                return env.Trim();
            try
            {
                var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NurMarketKassa", "remote-relay.txt");
                // 2026-10-16: пустой файл больше не выключает поддержку — берётся боевой адрес.
                if (File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } fromFile)
                    return fromFile;
            }
            catch
            {
                // нет доступа к файлу — боевой адрес
            }
            return DefaultRelayUrl;
        }
    }

    /// <summary>2026-10-16: боевой адрес ретранслятора на нашем VPS; путь хоста — /ws/host.</summary>
    public const string DefaultRelayUrl = "wss://support.nurcrm.kg/ws/host";

    /// <summary>2026-10-16: телефон поддержки NurMarket (тот же, что в окне «Поддержка», AdminSupportWindow) — для сообщения
    /// «сервер поддержки недоступен».</summary>
    public const string SupportPhone = "+996 559 340 032";

    /// <summary>2026-10-16: сервер поддержки недоступен — что делать кассиру.</summary>
    public static string UnreachableText() => Tr.T(
        $"Сервер поддержки недоступен — позвоните в поддержку по телефону {SupportPhone} (WhatsApp — тот же номер).",
        $"Колдоо сервери жеткиликсиз — колдоо кызматына {SupportPhone} телефону менен чалыңыз (WhatsApp — ушул эле номер).",
        $"The support server is unavailable — call support at {SupportPhone} (WhatsApp — the same number).",
        $"Destek sunucusuna ulaşılamıyor — destek hattını {SupportPhone} numarasından arayın (WhatsApp — aynı numara).",
        $"Qo'llab-quvvatlash serveri mavjud emas — yordam xizmatiga {SupportPhone} raqami orqali qo'ng'iroq qiling (WhatsApp — shu raqam).");

    public event Action<State>? StateChanged;
    public event Action<string, string, int>? CodeReceived;          // код, PIN, секунд до истечения
    public event Func<string, Task<Consent>>? ConsentRequested;       // 2026-10-15: имя оператора → ответ кассира (было bool)
    public event Action<string>? SessionStarted;                      // имя оператора
    public event Action<string>? Ended;                               // причина словами

    public State Current { get; private set; } = State.Connecting;
    public string Operator { get; private set; } = "";
    public bool ViewOnly => _viewOnly || _localViewOnly;

    private readonly ClientWebSocket _ws = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _ackSignal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<ushort> _keysDown = new();
    private readonly HashSet<int> _buttonsDown = new();
    private volatile bool _viewOnly;
    // 2026-10-11, проверка безопасности: согласие кассира — только своё, локальное. Без него «started» от сервера
    // не включает показ экрана и управление (сервер с ошибкой или подменённый не подключится без «Разрешить»).
    private volatile bool _consentGranted;
    // 2026-10-15: «Только просмотр» кассира — ввод оператора не исполняется; сервер («view-only» false) это не снимает.
    private volatile bool _localViewOnly;
    // 2026-10-15: согласие привязано к одному запросу и одному оператору: номер запроса, имя одобренного оператора;
    // замок — чтобы «started» и истечение ожидания после «Разрешить» не перепутались.
    private readonly object _consentLock = new();
    private int _consentRound;
    private string _approvedOperator = "";
    private const int StartAfterConsentTimeoutMs = 30_000;
    // 2026-10-16: сколько ждём код от сервера (подключение + «session»). Не пришёл — конец с понятной ошибкой, а не вечное «Подключаюсь…».
    private const int ConnectingTimeoutMs = 30_000;
    private volatile bool _connectTimedOut;
    private volatile bool _forceFull = true;
    private volatile int _quality = 60;
    private uint _frameNo;
    private long _lastAck;
    private Thread? _streamThread;
    private int _screenW, _screenH;

    private void SetState(State s)
    {
        Current = s;
        StateChanged?.Invoke(s);
    }

    /// <summary>Подключиться к ретранслятору и получить код. Завершается, когда сеанс (или ожидание) закончился.</summary>
    public async Task RunAsync(string company, string version)
    {
        var ct = _cts.Token;
        _screenW = GetSystemMetrics(SM_CXSCREEN);
        _screenH = GetSystemMetrics(SM_CYSCREEN);
        var endReason = "";
        // 2026-10-16: за 30 с не подключились или сервер не прислал код — сеанс обрывается (причина — в finally).
        using var connectTimeout = new CancellationTokenSource(ConnectingTimeoutMs);
        using var connectTimeoutReg = connectTimeout.Token.Register(() =>
        {
            if (Current != State.Connecting)
                return;
            _connectTimedOut = true;
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // сеанс уже закрыт
            }
        });
        try
        {
            SetState(State.Connecting);
            var relay = new Uri(RelayUrl);
            // 2026-10-11: без шифрования (ws://) — только сервер на этом же ПК (проверка); иначе экран и ввод шли бы открыто.
            if (relay.Scheme != "wss" && !(relay.Scheme == "ws" && relay.IsLoopback))
                throw new InvalidOperationException("адрес сервера поддержки должен начинаться с wss://");
            await _ws.ConnectAsync(relay, ct).ConfigureAwait(false);
            await SendJsonAsync(new
            {
                type = "hello", role = "host", protocol = 1, app = "NurMarketKassa", version, company,
                machine = Environment.MachineName, screen = new { w = _screenW, h = _screenH },
            }, ct).ConfigureAwait(false);
            var buf = new byte[64 * 1024];
            while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close)
                        break;
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Close)
                    break;
                if (r.MessageType != WebSocketMessageType.Text)
                    continue; // двоичное хосту в v1 не положено
                JsonElement m;
                try
                {
                    m = JsonDocument.Parse(ms.ToArray()).RootElement;
                }
                catch (JsonException)
                {
                    continue;
                }
                // 2026-10-15: не объект или «type» не строка — пропустить сообщение (раньше исключение рвало сеанс).
                if (m.ValueKind != JsonValueKind.Object)
                    continue;
                var type = Str(m, "type");
                switch (type)
                {
                    case "ping":
                        await SendJsonAsync(new { type = "pong" }, ct).ConfigureAwait(false);
                        break;
                    case "session":
                        // 2026-10-15: кривое сообщение без кода — пропустить, а не обрывать связь «Нет связи».
                        // 2026-10-16: код и PIN принимаются и строкой, и числом. Без них, пока кода ещё нет (Connecting), —
                        // конец с понятной ошибкой: раньше сообщение пропускалось и окно навсегда оставалось «Подключаюсь…».
                        if (StrOrNum(m, "code") is not { } sessCode || StrOrNum(m, "pin") is not { } sessPin)
                        {
                            Malformed(type);
                            if (Current == State.Connecting)
                                endReason = Tr.T("Сервер поддержки прислал неверный код подключения. Попробуйте ещё раз.",
                                    "Колдоо сервери туура эмес туташуу кодун жөнөттү. Кайра аракет кылыңыз.",
                                    "The support server sent an invalid connection code. Please try again.",
                                    "Destek sunucusu geçersiz bir bağlantı kodu gönderdi. Lütfen tekrar deneyin.",
                                    "Qo'llab-quvvatlash serveri noto'g'ri ulanish kodini yubordi. Qayta urinib ko'ring.");
                            break;
                        }
                        SetState(State.WaitingOperator);
                        CodeReceived?.Invoke(sessCode, sessPin, TryNum(m, "expiresIn", out var exp) ? (int)Math.Clamp(exp, 0, 86_400) : 900);
                        break;
                    case "join-request":
                        if (Current is State.InSession or State.AskingConsent)
                            break; // 2026-10-11: второй запрос во время сеанса или вопроса кассиру — не сбрасывает их
                        _ = AskConsentAsync(Str(m, "operator") ?? "", ct);
                        break;
                    case "started":
                        // 2026-10-15: сеанс — только по согласию на ЭТОТ запрос (ждём «started» сразу после «Разрешить») и только
                        // для одобренного оператора: другое имя в «started» — отклонено, сеанс не начинается.
                        var startedOp = Str(m, "operator");
                        var startOk = false;
                        lock (_consentLock)
                        {
                            if (_consentGranted && Current == State.AskingConsent
                                && (startedOp is null || string.Equals(startedOp, _approvedOperator, StringComparison.Ordinal)))
                            {
                                _consentGranted = false; // 2026-10-15: согласие одноразовое
                                Operator = startedOp ?? _approvedOperator;
                                SetState(State.InSession);
                                startOk = true;
                            }
                        }
                        if (!startOk)
                        {
                            PosLogger.Log($"Удалённая помощь: сервер начал сеанс без согласия кассира (оператор «{startedOp}», одобрен «{_approvedOperator}») — отклонено.", "WARNING");
                            endReason = Tr.T("Сеанс отклонён: кассир не давал разрешения.", "Сеанс четке кагылды: кассир уруксат берген жок.",
                                "The session was rejected: the cashier did not allow it.", "Oturum reddedildi: kasiyer izin vermedi.",
                                "Seans rad etildi: kassir ruxsat bermagan.");
                            break;
                        }
                        PosLogger.Log($"Удалённая помощь: сеанс начат, оператор «{Operator}»{(_localViewOnly ? ", только просмотр" : "")}.", "INFO");
                        SessionStarted?.Invoke(Operator);
                        _streamThread = new Thread(() => StreamLoop(ct)) { IsBackground = true, Name = "RemoteSupportStream" };
                        _streamThread.Start();
                        break;
                    case "expired":
                        endReason = Tr.T("Код истёк — никто не подключился за 15 минут.", "Код мөөнөтү бүттү — 15 мүнөттө эч ким туташкан жок.",
                            "The code expired — nobody connected within 15 minutes.", "Kodun süresi doldu — 15 dakikada kimse bağlanmadı.",
                            "Kod muddati tugadi — 15 daqiqada hech kim ulanmadi.");
                        break;
                    case "locked":
                        endReason = Tr.T("Заблокировано: 5 раз ввели неверный PIN. Получите новый код.", "Бөгөттөлдү: PIN 5 жолу туура эмес киргизилди. Жаңы код алыңыз.",
                            "Locked: a wrong PIN was entered 5 times. Get a new code.", "Kilitlendi: PIN 5 kez yanlış girildi. Yeni kod alın.",
                            "Bloklandi: PIN 5 marta noto'g'ri kiritildi. Yangi kod oling.");
                        break;
                    case "error":
                        endReason = Tr.T("Сервер поддержки ответил ошибкой: ", "Колдоо сервери ката менен жооп берди: ", "The support server returned an error: ",
                            "Destek sunucusu hata döndürdü: ", "Qo'llab-quvvatlash serveri xato qaytardi: ") + (m.TryGetProperty("code", out var c) ? c.GetString() : "?");
                        break;
                    case "end":
                        endReason = EndText(m.TryGetProperty("reason", out var rs) ? rs.GetString() ?? "" : "");
                        break;
                    // 2026-10-15: ack/quality/view-only без нужных полей (или не того типа) раньше бросали исключение и рвали
                    // весь сеанс с «Нет связи» — теперь кривое сообщение пишется в журнал и пропускается.
                    case "ack":
                        if (!TryNum(m, "frame", out var f))
                        {
                            Malformed(type);
                            break;
                        }
                        if (f > Interlocked.Read(ref _lastAck))
                            Interlocked.Exchange(ref _lastAck, f);
                        _ackSignal.Release();
                        break;
                    // 2026-10-15: ввод — только в сеансе и не в «только просмотр» (ни серверном, ни выбранном кассиром);
                    // поле не того типа у мыши/клавиши — пропустить сообщение, а не оборвать сеанс.
                    case "mouse":
                        if (Current == State.InSession && !_viewOnly && !_localViewOnly)
                        {
                            try
                            {
                                OnMouse(m);
                            }
                            catch (InvalidOperationException)
                            {
                                Malformed(type);
                            }
                        }
                        break;
                    case "key":
                        if (Current == State.InSession && !_viewOnly && !_localViewOnly)
                        {
                            try
                            {
                                OnKey(m);
                            }
                            catch (InvalidOperationException)
                            {
                                Malformed(type);
                            }
                        }
                        break;
                    case "text":
                        if (Current == State.InSession && !_viewOnly && !_localViewOnly && Str(m, "text") is { } tx)
                            TypeText(tx);
                        break;
                    case "quality":
                        if (!TryNum(m, "value", out var qv))
                        {
                            Malformed(type);
                            break;
                        }
                        _quality = (int)Math.Clamp(qv, 10, 90);
                        _forceFull = true;
                        break;
                    case "view-only":
                        if (!m.TryGetProperty("value", out var vo) || vo.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        {
                            Malformed(type);
                            break;
                        }
                        _viewOnly = vo.GetBoolean();
                        if (_viewOnly)
                            ReleaseAll();
                        break;
                }
                if (endReason.Length > 0)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            endReason = Tr.T("Сеанс завершён на кассе.", "Сеанс кассада аяктады.", "The session was ended on this till.", "Oturum bu kasada sonlandırıldı.",
                "Seans shu kassada tugatildi.");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Удалённая помощь: соединение не удалось ({ex.Message}).", "WARNING");
            // 2026-10-16: до кода сервер не ответил (нет сети, DNS, сервер выключен, 502) — понятное сообщение с телефоном поддержки.
            endReason = Current == State.Connecting && ex is WebSocketException or System.Net.Http.HttpRequestException or System.Net.Sockets.SocketException or IOException
                ? UnreachableText()
                : Tr.T("Нет связи с сервером поддержки: ", "Колдоо сервери менен байланыш жок: ", "No connection to the support server: ",
                    "Destek sunucusuna bağlantı yok: ", "Qo'llab-quvvatlash serveri bilan aloqa yo'q: ") + ex.Message;
        }
        finally
        {
            ReleaseAll();
            var wasSession = Current == State.InSession;
            SetState(State.Ended);
            _ackSignal.Release();
            if (_ws.State == WebSocketState.Open)
            {
                try
                {
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // соединение уже оборвано
                }
            }
            // 2026-10-16: оборвали по таймауту подключения — так и пишем (а не «Сеанс завершён на кассе» от отмены).
            // 2026-10-16: + телефон поддержки — других способов удалённой помощи в кассе нет.
            if (_connectTimedOut)
                endReason = Tr.T($"Сервер поддержки не ответил за 30 секунд. Проверьте интернет и попробуйте ещё раз или позвоните в поддержку по телефону {SupportPhone}.",
                    $"Колдоо сервери 30 секунданын ичинде жооп берген жок. Интернетти текшерип, кайра аракет кылыңыз же колдоо кызматына {SupportPhone} телефону менен чалыңыз.",
                    $"The support server did not respond within 30 seconds. Check the internet connection and try again, or call support at {SupportPhone}.",
                    $"Destek sunucusu 30 saniye içinde yanıt vermedi. İnternet bağlantısını kontrol edip tekrar deneyin veya destek hattını {SupportPhone} numarasından arayın.",
                    $"Qo'llab-quvvatlash serveri 30 soniya ichida javob bermadi. Internetni tekshirib, qayta urinib ko'ring yoki yordam xizmatiga {SupportPhone} raqami orqali qo'ng'iroq qiling.");
            if (endReason.Length == 0)
                endReason = wasSession
                    ? Tr.T("Сеанс поддержки завершён.", "Колдоо сеансы аяктады.", "The support session has ended.", "Destek oturumu sona erdi.", "Qo'llab-quvvatlash seansi tugadi.")
                    : Tr.T("Соединение с сервером поддержки закрыто.", "Колдоо сервери менен байланыш жабылды.", "The connection to the support server was closed.",
                        "Destek sunucusuyla bağlantı kapandı.", "Qo'llab-quvvatlash serveri bilan aloqa yopildi.");
            PosLogger.Log($"Удалённая помощь: конец — {endReason}", "INFO");
            Ended?.Invoke(endReason);
        }
    }

    private static string EndText(string reason) => reason switch
    {
        "declined" => Tr.T("Вы отказали оператору в доступе.", "Сиз операторго уруксат берген жоксуз.", "You declined the operator's access.", "Operatörün erişimini reddettiniz.", "Siz operatorga ruxsat bermadingiz."),
        "consent-timeout" => Tr.T("Нет ответа на запрос оператора — доступ не дан.", "Оператордун суроосуна жооп болгон жок — уруксат берилген жок.", "No answer to the operator's request — access was not granted.", "Operatör isteğine yanıt verilmedi — erişim verilmedi.", "Operator so'roviga javob bo'lmadi — ruxsat berilmadi."),
        "operator" => Tr.T("Оператор завершил сеанс.", "Оператор сеансты аяктады.", "The operator ended the session.", "Operatör oturumu sonlandırdı.", "Operator seansni tugatdi."),
        "host" => Tr.T("Сеанс завершён на кассе.", "Сеанс кассада аяктады.", "The session was ended on this till.", "Oturum bu kasada sonlandırıldı.", "Seans shu kassada tugatildi."),
        "timeout" or "disconnect" => Tr.T("Связь с оператором прервалась.", "Оператор менен байланыш үзүлдү.", "The connection to the operator was lost.", "Operatörle bağlantı koptu.", "Operator bilan aloqa uzildi."),
        _ => Tr.T("Сеанс поддержки завершён.", "Колдоо сеансы аяктады.", "The support session has ended.", "Destek oturumu sona erdi.", "Qo'llab-quvvatlash seansi tugadi."),
    };

    private async Task AskConsentAsync(string op, CancellationToken ct)
    {
        int round;
        lock (_consentLock)
        {
            round = ++_consentRound;
            _consentGranted = false;
            SetState(State.AskingConsent);
        }
        var consent = Consent.Deny;
        try
        {
            if (ConsentRequested is { } ask)
            {
                // Кассир думает не дольше 60 с (сервер сам отказывает через 90 с).
                var answer = ask(op);
                // 2026-10-15: упавший вопрос (ошибка окна) — отказ, а не исключение без ответа серверу.
                consent = await Task.WhenAny(answer, Task.Delay(60_000, ct)).ConfigureAwait(false) == answer && answer.IsCompletedSuccessfully
                    ? answer.Result
                    : Consent.Deny;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        var accept = consent != Consent.Deny;
        var said = consent switch { Consent.Allow => "разрешено", Consent.ViewOnly => "разрешён только просмотр", _ => "отказано" };
        PosLogger.Log($"Удалённая помощь: оператор «{op}» — {said} кассиром.", "INFO");
        lock (_consentLock)
        {
            if (round != _consentRound || Current != State.AskingConsent)
                return; // 2026-10-15: сеанс уже закончился или это чужой (устаревший) запрос
            _consentGranted = accept;
            _localViewOnly = consent != Consent.Allow;
            _approvedOperator = op;
            if (!accept)
                SetState(State.WaitingOperator);
        }
        // 2026-10-16: «Только просмотр» — в ответе поле view_only: true. Раньше уходило просто accept: true, и оператор
        // не знал, что управления не будет. В протоколе v1 такого поля нет: показать его оператору должен сервер — это ещё
        // не сделано, внести в ТЗ сервера удалённой поддержки (лишнее поле сервер v1 пропускает). Отдельного сообщения
        // «только просмотр» от кассы к серверу в v1 тоже нет — «view-only» идёт только от сервера. Касса в любом случае
        // сама не исполняет ввод (_localViewOnly).
        var reply = consent == Consent.ViewOnly
            ? (object)new { type = "consent", accept, view_only = true }
            : new { type = "consent", accept };
        await SendJsonAsync(reply, ct).ConfigureAwait(false);
        if (accept)
            _ = ExpireConsentAsync(round, ct);
    }

    /// <summary>2026-10-15: после «Разрешить» сервер должен начать сеанс сразу. Не начал за 30 с (оператор ушёл) — согласие
    /// снимается и касса снова ждёт оператора: новый запрос опять спросит кассира, а не будет молча проигнорирован до истечения кода.</summary>
    private async Task ExpireConsentAsync(int round, CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartAfterConsentTimeoutMs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        lock (_consentLock)
        {
            if (round != _consentRound || !_consentGranted || Current != State.AskingConsent)
                return;
            _consentGranted = false;
            _approvedOperator = "";
            SetState(State.WaitingOperator);
        }
        PosLogger.Log("Удалённая помощь: после «Разрешить» сеанс не начался за 30 с — согласие снято, ждём оператора снова.", "WARNING");
        // 2026-10-16: согласие снято и на сервере — отказом. Раньше снималось только на кассе: сервер считал доступ
        // данным, поздний «started» касса отклоняла и рвала всю связь с «кассир не давал разрешения». Ошибка отправки
        // (связь уже рвётся) — не исключение без наблюдателя: конец сеанса покажет основной цикл.
        try
        {
            await SendJsonAsync(new { type = "consent", accept = false }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Удалённая помощь: отказ после истечения согласия не отправлен ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>2026-10-15: строковое поле сообщения или null (нет поля / не строка) — без исключений.</summary>
    private static string? Str(JsonElement m, string name) =>
        m.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    /// <summary>2026-10-16: поле-строка или целое число (код и PIN сервер может прислать числом) — как текст; пустое,
    /// нет поля или другой тип — null.</summary>
    private static string? StrOrNum(JsonElement m, string name)
    {
        if (!m.TryGetProperty(name, out var e))
            return null;
        var s = e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number when e.TryGetInt64(out var n) && n >= 0 => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    /// <summary>2026-10-15: целое поле сообщения — без исключений на отсутствующее поле или не число.</summary>
    private static bool TryNum(JsonElement m, string name, out long value)
    {
        value = 0;
        return m.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out value);
    }

    private static void Malformed(string? type) =>
        PosLogger.Log($"Удалённая помощь: сообщение «{type}» без нужных полей — пропущено.", "WARNING");

    /// <summary>Завершить сеанс или ожидание кнопкой кассира.</summary>
    public async Task StopAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
                await SendJsonAsync(new { type = "end", reason = "host" }, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // уже закрыто
        }
        _cts.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _ws.Dispose();
    }

    // ---------- поток экрана (отдельный поток: GDI и SendAsync синхронно) ----------

    private void StreamLoop(CancellationToken ct)
    {
        const int tile = 128;
        const int fps = 10;
        try
        {
            int w = _screenW, h = _screenH;
            SendJsonAsync(new { type = "screen", w, h, monitor = 0, monitors = 1 }, ct).GetAwaiter().GetResult();
            using var cur = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            byte[]? curBytes = null, prevBytes = null;
            var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            var interval = TimeSpan.FromSeconds(1.0 / fps);
            var lastCursor = new POINT { X = -1, Y = -1 };
            while (Current == State.InSession && !ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                var tick = Stopwatch.StartNew();
                while (_frameNo - Interlocked.Read(ref _lastAck) >= 2 && Current == State.InSession && !ct.IsCancellationRequested)
                    _ackSignal.Wait(500, ct);
                if (Current != State.InSession)
                    break;
                using (var g = Graphics.FromImage(cur))
                    g.CopyFromScreen(0, 0, 0, 0, new Size(w, h));
                var data = cur.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                int stride;
                try
                {
                    stride = data.Stride;
                    curBytes ??= new byte[stride * h];
                    prevBytes ??= new byte[stride * h];
                    Marshal.Copy(data.Scan0, curBytes, 0, stride * h);
                }
                finally
                {
                    cur.UnlockBits(data);
                }
                var full = _forceFull;
                _forceFull = false;
                var changed = new List<Rectangle>();
                for (var ty = 0; ty < h; ty += tile)
                for (var tx = 0; tx < w; tx += tile)
                {
                    var rect = new Rectangle(tx, ty, Math.Min(tile, w - tx), Math.Min(tile, h - ty));
                    if (full || TileChanged(curBytes, prevBytes, stride, rect))
                        changed.Add(rect);
                }
                (prevBytes, curBytes) = (curBytes, prevBytes);
                if (changed.Count > 0)
                {
                    var frame = ++_frameNo;
                    using var q = new EncoderParameters(1);
                    q.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)_quality);
                    foreach (var rect in changed)
                    {
                        using var part = cur.Clone(rect, PixelFormat.Format24bppRgb);
                        using var ms = new MemoryStream();
                        part.Save(ms, jpeg, q);
                        SendAsync(Packet(1, frame, rect, ms.GetBuffer().AsSpan(0, (int)ms.Length)), WebSocketMessageType.Binary, ct).GetAwaiter().GetResult();
                    }
                    SendAsync(Packet(2, frame, Rectangle.Empty, ReadOnlySpan<byte>.Empty), WebSocketMessageType.Binary, ct).GetAwaiter().GetResult();
                }
                if (GetCursorPos(out var c) && (c.X != lastCursor.X || c.Y != lastCursor.Y))
                {
                    lastCursor = c;
                    SendJsonAsync(new { type = "cursor", x = c.X, y = c.Y }, ct).GetAwaiter().GetResult();
                }
                var left = interval - tick.Elapsed;
                if (left > TimeSpan.Zero)
                    Thread.Sleep(left);
            }
        }
        catch (OperationCanceledException)
        {
            // сеанс завершён
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Удалённая помощь: поток экрана остановлен ({ex.Message}).", "WARNING");
            _ = StopAsync();
        }
    }

    private static bool TileChanged(byte[] cur, byte[] prev, int stride, Rectangle r)
    {
        var len = r.Width * 3;
        for (var y = r.Top; y < r.Bottom; y++)
        {
            var off = y * stride + r.Left * 3;
            if (!cur.AsSpan(off, len).SequenceEqual(prev.AsSpan(off, len)))
                return true;
        }
        return false;
    }

    private static byte[] Packet(byte kind, uint frame, Rectangle r, ReadOnlySpan<byte> jpeg)
    {
        var p = new byte[13 + jpeg.Length];
        p[0] = kind;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(1), frame);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(5), (ushort)r.X);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(7), (ushort)r.Y);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(9), (ushort)r.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(11), (ushort)r.Height);
        jpeg.CopyTo(p.AsSpan(13));
        return p;
    }

    private Task SendJsonAsync(object o, CancellationToken ct) =>
        SendAsync(JsonSerializer.SerializeToUtf8Bytes(o), WebSocketMessageType.Text, ct);

    private async Task SendAsync(byte[] data, WebSocketMessageType type, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.SendAsync(data, type, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // ---------- ввод оператора ----------

    private void OnMouse(JsonElement m)
    {
        var action = m.TryGetProperty("action", out var a) ? a.GetString() : "";
        var x = m.TryGetProperty("x", out var xe) && xe.TryGetInt32(out var xv) ? xv : -1;
        var y = m.TryGetProperty("y", out var ye) && ye.TryGetInt32(out var yv) ? yv : -1;
        if (x < 0 || y < 0 || x >= _screenW || y >= _screenH)
            return;
        var nx = x * 65535 / Math.Max(1, _screenW - 1);
        var ny = y * 65535 / Math.Max(1, _screenH - 1);
        var button = m.TryGetProperty("button", out var b) && b.TryGetInt32(out var bv) ? bv : 0;
        uint flags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE;
        var data = 0;
        switch (action)
        {
            case "down":
                flags |= button switch { 2 => MOUSEEVENTF_RIGHTDOWN, 1 => MOUSEEVENTF_MIDDLEDOWN, _ => MOUSEEVENTF_LEFTDOWN };
                _buttonsDown.Add(button);
                break;
            case "up":
                flags |= button switch { 2 => MOUSEEVENTF_RIGHTUP, 1 => MOUSEEVENTF_MIDDLEUP, _ => MOUSEEVENTF_LEFTUP };
                _buttonsDown.Remove(button);
                break;
            case "wheel":
                flags |= MOUSEEVENTF_WHEEL;
                data = m.TryGetProperty("delta", out var d) && d.TryGetInt32(out var dv) ? Math.Clamp(dv, -1200, 1200) : 0;
                break;
            case "move":
                break;
            default:
                return;
        }
        SendMouse(nx, ny, flags, data);
    }

    private static void SendMouse(int nx, int ny, uint flags, int data)
    {
        var input = new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dx = nx, dy = ny, mouseData = data, dwFlags = flags } } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private void OnKey(JsonElement m)
    {
        var action = m.TryGetProperty("action", out var a) ? a.GetString() : "";
        var code = m.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
        if (Vk(code) is not { } vk)
        {
            // Неизвестная клавиша с печатным символом — как текст.
            if (action == "down" && m.TryGetProperty("key", out var k) && k.GetString() is { Length: 1 } ch)
                TypeText(ch);
            return;
        }
        var up = action == "up";
        if (up)
            _keysDown.Remove(vk.Code);
        else
            _keysDown.Add(vk.Code);
        SendKey(vk.Code, vk.Extended, up);
    }

    private static void SendKey(ushort vk, bool extended, bool up)
    {
        var flags = (up ? KEYEVENTF_KEYUP : 0u) | (extended ? KEYEVENTF_EXTENDEDKEY : 0u);
        var input = new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKey(vk, 0), dwFlags = flags } } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void TypeText(string text)
    {
        if (text.Length > 10_000)
            text = text[..10_000];
        foreach (var ch in text.Replace("\r\n", "\n"))
        {
            if (ch == '\n')
            {
                SendKey(0x0D, false, false);
                SendKey(0x0D, false, true);
                continue;
            }
            var down = new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE } } };
            var upIn = new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } };
            SendInput(2, new[] { down, upIn }, Marshal.SizeOf<INPUT>());
        }
    }

    /// <summary>Конец сеанса или «только просмотр» — отпустить всё, что оператор держал нажатым.</summary>
    private void ReleaseAll()
    {
        try
        {
            foreach (var vk in _keysDown.ToList())
                SendKey(vk, false, true);
            _keysDown.Clear();
            foreach (var b in _buttonsDown.ToList())
                SendMouse(0, 0, b switch { 2 => MOUSEEVENTF_RIGHTUP, 1 => MOUSEEVENTF_MIDDLEUP, _ => MOUSEEVENTF_LEFTUP }, 0);
            _buttonsDown.Clear();
        }
        catch
        {
            // ввод недоступен — ничего не держим
        }
    }

    /// <summary>KeyboardEvent.code → виртуальная клавиша Windows.</summary>
    private static (ushort Code, bool Extended)? Vk(string code)
    {
        if (code.Length == 4 && code.StartsWith("Key") && char.IsAsciiLetterUpper(code[3]))
            return ((ushort)code[3], false);
        if (code.Length == 6 && code.StartsWith("Digit") && char.IsAsciiDigit(code[5]))
            return ((ushort)code[5], false);
        if (code.StartsWith("Numpad") && code.Length == 7 && char.IsAsciiDigit(code[6]))
            return ((ushort)(0x60 + code[6] - '0'), false);
        if (code.Length is 2 or 3 && code[0] == 'F' && int.TryParse(code[1..], out var fn) && fn is >= 1 and <= 12)
            return ((ushort)(0x6F + fn), false);
        return code switch
        {
            "Enter" => (0x0D, false), "NumpadEnter" => (0x0D, true), "Escape" => (0x1B, false), "Backspace" => (0x08, false), "Tab" => (0x09, false),
            "Space" => (0x20, false), "ArrowLeft" => (0x25, true), "ArrowUp" => (0x26, true), "ArrowRight" => (0x27, true), "ArrowDown" => (0x28, true),
            "Home" => (0x24, true), "End" => (0x23, true), "PageUp" => (0x21, true), "PageDown" => (0x22, true), "Insert" => (0x2D, true), "Delete" => (0x2E, true),
            "ShiftLeft" => (0xA0, false), "ShiftRight" => (0xA1, false), "ControlLeft" => (0xA2, false), "ControlRight" => (0xA3, true),
            "AltLeft" => (0xA4, false), "AltRight" => (0xA5, true), "MetaLeft" => (0x5B, true), "MetaRight" => (0x5C, true), "CapsLock" => (0x14, false),
            "Minus" => (0xBD, false), "Equal" => (0xBB, false), "BracketLeft" => (0xDB, false), "BracketRight" => (0xDD, false), "Backslash" => (0xDC, false),
            "Semicolon" => (0xBA, false), "Quote" => (0xDE, false), "Backquote" => (0xC0, false), "Comma" => (0xBC, false), "Period" => (0xBE, false),
            "Slash" => (0xBF, false), "NumpadAdd" => (0x6B, false), "NumpadSubtract" => (0x6D, false), "NumpadMultiply" => (0x6A, false),
            "NumpadDivide" => (0x6F, true), "NumpadDecimal" => (0x6E, false), "PrintScreen" => (0x2C, true), "ContextMenu" => (0x5D, true),
            _ => null,
        };
    }

    // ---------- Win32 ----------

    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004, MOUSEEVENTF_RIGHTDOWN = 0x0008,
        MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040, MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy, mouseData; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
}
#endif
