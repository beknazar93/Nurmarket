using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NurMarketKassa.Services;

/// <summary>2026-11-27 (просьба владельца «открытие кассы с помощью QR — при сканере сразу входит от имени кассира»):
/// QR-бейдж кассира. Содержимое QR — НЕ пароль и НЕ токен, а случайный код «NMBADGE:v1:&lt;id&gt;:&lt;секрет&gt;»
/// (id — 48 бит, секрет — 128 бит, шестнадцатеричные). Нигде, кроме самого бейджа, секрет не хранится: в реестре — только
/// SHA-256. Сканер-клавиатура в русской/кыргызской раскладке печатает буквы ЙЦУКЕН — разбор переводит их обратно по клавишам
/// (как <see cref="ClientQrCode"/>), «:» в этой раскладке приходит как «Ж» (→ «;»), поэтому «;» считается тем же разделителем.</summary>
public static class CashierBadgeCode
{
    public const string Prefix = "NMBADGE";
    public const string Version = "v1";
    public const int IdHexLength = 12;
    public const int SecretHexLength = 32;

    /// <summary>Новый код бейджа: (код для QR, id, секрет). Секрет показывается владельцу один раз — в QR.</summary>
    public static (string Code, string Id, string Secret) Generate()
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(IdHexLength / 2)).ToLowerInvariant();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(SecretHexLength / 2)).ToLowerInvariant();
        return (Format(id, secret), id, secret);
    }

    public static string Format(string id, string secret) => $"{Prefix}:{Version}:{id}:{secret}";

    /// <summary>Строка похожа на бейдж (есть префикс NMBADGE в любой раскладке) — даже если дальше мусор. Такую строку
    /// касса не ищет как товар и не пишет в журнал.</summary>
    public static bool LooksLikeBadge(string? raw) => IndexOfPrefix(raw) >= 0;

    /// <summary>Из ввода сканера (могли быть набранные раньше буквы) — часть, начиная с префикса бейджа; null — бейджа нет.</summary>
    public static string? ExtractFromInput(string? raw)
    {
        var i = IndexOfPrefix(raw);
        return i < 0 ? null : raw![i..].Trim();
    }

    private static int IndexOfPrefix(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length < Prefix.Length)
            return -1;
        return ToLatinLayout(raw).IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <returns>true — строка в формате бейджа v1; id и секрет в нижнем регистре.</returns>
    public static bool TryParse(string? raw, out string id, out string secret)
    {
        id = "";
        secret = "";
        var text = ExtractFromInput(raw);
        if (text is null)
            return false;
        var parts = ToLatinLayout(text).Replace(';', ':').Split(':');
        if (parts.Length != 4
            || !string.Equals(parts[0], Prefix, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(parts[1], Version, StringComparison.OrdinalIgnoreCase))
            return false;
        var pid = parts[2].Trim().ToLowerInvariant();
        var psec = parts[3].Trim().ToLowerInvariant();
        if (pid.Length != IdHexLength || psec.Length != SecretHexLength || !IsHex(pid) || !IsHex(psec))
            return false;
        id = pid;
        secret = psec;
        return true;
    }

    /// <summary>Хэш секрета для реестра (SHA-256, шестнадцатеричный). Секрет 128 бит — перебор хэша невозможен, соль не нужна.</summary>
    public static string HashSecret(string id, string secret)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{Prefix}|{Version}|{id.ToLowerInvariant()}|{secret.ToLowerInvariant()}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Сравнение хэшей за постоянное время.</summary>
    public static bool HashEquals(string? a, string? b)
    {
        if (a is null || b is null)
            return false;
        var x = Encoding.ASCII.GetBytes(a.ToLowerInvariant());
        var y = Encoding.ASCII.GetBytes(b.ToLowerInvariant());
        return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y);
    }

    /// <summary>Для журнала: «a1b2****» — только начало id, секрет никогда.</summary>
    public static string Mask(string? id) => string.IsNullOrEmpty(id) ? "????" : (id.Length > 4 ? id[..4] : id) + "****";

    private static bool IsHex(string s) => s.All(Uri.IsHexDigit);

    // Клавиши ЙЦУКЕН → QWERTY (русская и кыргызская раскладки совпадают на этих клавишах), как в ClientQrCode.
    private const string CyrillicKeys = "йцукенгшщзхъфывапролджэячсмитьбюё";
    private const string LatinKeys = "qwertyuiop[]asdfghjkl;'zxcvbnm,.`";

    private static string ToLatinLayout(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            var index = CyrillicKeys.IndexOf(char.ToLowerInvariant(c));
            if (index < 0)
            {
                sb.Append(c);
                continue;
            }
            var latin = LatinKeys[index];
            sb.Append(char.IsUpper(c) ? char.ToUpperInvariant(latin) : latin);
        }
        return sb.ToString();
    }
}

/// <summary>2026-11-27: запись реестра бейджей (общая настройка магазина <see cref="UserPreferences.CashierBadgesJson"/>, раздаётся
/// на все кассы облаком). Секрета и пароля здесь нет — только хэш. Отозванные записи остаются (касса скажет «отозван», а не «не найден»).</summary>
public sealed class CashierBadgeRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>id сотрудника на сервере (ServerId) — от его имени касса и входит.</summary>
    [JsonPropertyName("employee_id")] public string EmployeeId { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    /// <summary>Логин (почта) сотрудника — подставляется в окно входа, когда на этой кассе нужен первый вход паролем.</summary>
    [JsonPropertyName("login")] public string? Login { get; set; }
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("created_at")] public long CreatedAt { get; set; }
    [JsonPropertyName("expires_at")] public long? ExpiresAt { get; set; }
    [JsonPropertyName("revoked_at")] public long? RevokedAt { get; set; }
    /// <summary>«QR + PIN»: PBKDF2-SHA256 PIN-кода (соль и итерации рядом). Пусто — достаточно QR.</summary>
    [JsonPropertyName("pin_salt")] public string? PinSalt { get; set; }
    [JsonPropertyName("pin_hash")] public string? PinHash { get; set; }
    [JsonPropertyName("pin_iter")] public int? PinIterations { get; set; }

    [JsonIgnore] public bool RequiresPin => !string.IsNullOrEmpty(PinHash) && !string.IsNullOrEmpty(PinSalt);

    public bool IsActive(DateTimeOffset now) =>
        RevokedAt is null && (ExpiresAt is not { } exp || now.ToUnixTimeMilliseconds() < exp);
}

/// <summary>2026-11-27: результат проверки отсканированного бейджа.</summary>
public enum CashierBadgeStatus
{
    /// <summary>Не бейдж (обычный ввод).</summary>
    NotBadge,
    /// <summary>Префикс есть, формат неверный (обрезан, ошибка печати).</summary>
    Malformed,
    /// <summary>Такого бейджа нет в реестре этой кассы (не получен ещё или удалён).</summary>
    Unknown,
    WrongSecret,
    Revoked,
    Expired,
    /// <summary>Слишком много неудачных попыток — подождать.</summary>
    LockedOut,
    Ok,
}

public sealed record CashierBadgeCheck(CashierBadgeStatus Status, CashierBadgeRecord? Record = null, TimeSpan? Wait = null);

/// <summary>2026-11-27: реестр бейджей магазина — разбор/запись JSON и проверка кода (чистая логика, без файлов и сети).</summary>
public static class CashierBadgeRegistry
{
    /// <summary>Ключ общей настройки (SettingsCloudSync.CompanyKeys).</summary>
    public const string CloudKey = "CashierBadgesJson";

    /// <summary>Потолок размера JSON (как у других общих JSON-настроек) и числа записей.</summary>
    public const int MaxJsonBytes = 64 * 1024;
    public const int MaxRecords = 200;

    /// <summary>PBKDF2 для PIN: итерации и длины.</summary>
    public const int PinIterationsDefault = 100_000;
    public const int PinMinLength = 4;
    public const int PinMaxLength = 8;

    private static readonly JsonSerializerOptions JsonOpts = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private sealed class Doc
    {
        [JsonPropertyName("v")] public int V { get; set; } = 1;
        [JsonPropertyName("badges")] public List<CashierBadgeRecord> Badges { get; set; } = new();
    }

    public static List<CashierBadgeRecord> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxJsonBytes)
            return new List<CashierBadgeRecord>();
        try
        {
            var doc = JsonSerializer.Deserialize<Doc>(json, JsonOpts);
            return (doc?.Badges ?? new List<CashierBadgeRecord>())
                .Where(b => !string.IsNullOrWhiteSpace(b.Id) && !string.IsNullOrWhiteSpace(b.EmployeeId) && !string.IsNullOrWhiteSpace(b.Hash))
                .ToList();
        }
        catch (JsonException)
        {
            return new List<CashierBadgeRecord>();
        }
    }

    public static string Serialize(IEnumerable<CashierBadgeRecord> records)
    {
        // Самые новые — первыми; старые отозванные отбрасываются сверх потолка.
        var list = records.OrderByDescending(r => r.CreatedAt).Take(MaxRecords).ToList();
        return JsonSerializer.Serialize(new Doc { Badges = list }, JsonOpts);
    }

    /// <summary>Действующий бейдж сотрудника (последний выпущенный), если есть.</summary>
    public static CashierBadgeRecord? ActiveFor(IEnumerable<CashierBadgeRecord> records, string? employeeId, DateTimeOffset now) =>
        string.IsNullOrWhiteSpace(employeeId)
            ? null
            : records.Where(r => string.Equals(r.EmployeeId, employeeId, StringComparison.OrdinalIgnoreCase) && r.IsActive(now))
                .OrderByDescending(r => r.CreatedAt).FirstOrDefault();

    /// <summary>Выпустить новый бейдж сотруднику: прежние действующие отзываются (у сотрудника один действующий бейдж).
    /// Возвращает новый список и код для QR (показать один раз).</summary>
    public static (List<CashierBadgeRecord> Records, string Code, CashierBadgeRecord Record) Issue(
        IEnumerable<CashierBadgeRecord> current, string employeeId, string? name, string? login, DateTimeOffset now,
        TimeSpan? validFor = null, string? pin = null)
    {
        if (string.IsNullOrWhiteSpace(employeeId))
            throw new ArgumentException("employeeId");
        var list = current.ToList();
        foreach (var r in list.Where(r => string.Equals(r.EmployeeId, employeeId, StringComparison.OrdinalIgnoreCase) && r.RevokedAt is null))
            r.RevokedAt = now.ToUnixTimeMilliseconds();

        var (code, id, secret) = CashierBadgeCode.Generate();
        var record = new CashierBadgeRecord
        {
            Id = id,
            EmployeeId = employeeId.Trim(),
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            Login = string.IsNullOrWhiteSpace(login) ? null : login.Trim(),
            Hash = CashierBadgeCode.HashSecret(id, secret),
            CreatedAt = now.ToUnixTimeMilliseconds(),
            ExpiresAt = validFor is { } v ? now.Add(v).ToUnixTimeMilliseconds() : null,
        };
        if (!string.IsNullOrEmpty(pin))
            SetPin(record, pin);
        list.Insert(0, record);
        return (list, code, record);
    }

    /// <summary>Отозвать все действующие бейджи сотрудника; true — что-то отозвано.</summary>
    public static bool Revoke(IEnumerable<CashierBadgeRecord> records, string employeeId, DateTimeOffset now)
    {
        var any = false;
        foreach (var r in records.Where(r => string.Equals(r.EmployeeId, employeeId, StringComparison.OrdinalIgnoreCase) && r.RevokedAt is null))
        {
            r.RevokedAt = now.ToUnixTimeMilliseconds();
            any = true;
        }
        return any;
    }

    public static bool IsValidPin(string? pin) =>
        pin is { Length: >= PinMinLength and <= PinMaxLength } && pin.All(char.IsAsciiDigit);

    public static void SetPin(CashierBadgeRecord record, string pin)
    {
        if (!IsValidPin(pin))
            throw new ArgumentException("PIN");
        var salt = RandomNumberGenerator.GetBytes(16);
        record.PinSalt = Convert.ToBase64String(salt);
        record.PinIterations = PinIterationsDefault;
        record.PinHash = Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, PinIterationsDefault, HashAlgorithmName.SHA256, 32));
    }

    public static bool VerifyPin(CashierBadgeRecord record, string? pin)
    {
        if (!record.RequiresPin)
            return true;
        if (string.IsNullOrEmpty(pin) || pin.Length > 32)
            return false;
        try
        {
            var salt = Convert.FromBase64String(record.PinSalt!);
            var expected = Convert.FromBase64String(record.PinHash!);
            var iter = record.PinIterations is > 0 and <= 2_000_000 ? record.PinIterations.Value : PinIterationsDefault;
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin.Trim()), salt, iter, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Проверка отсканированной строки по реестру. Порядок: формат → запись → секрет → отзыв → срок
    /// (о сроке и отзыве говорим только тому, кто предъявил верный секрет).</summary>
    public static CashierBadgeCheck Verify(string? raw, IReadOnlyCollection<CashierBadgeRecord> records, DateTimeOffset now)
    {
        if (!CashierBadgeCode.LooksLikeBadge(raw))
            return new CashierBadgeCheck(CashierBadgeStatus.NotBadge);
        if (!CashierBadgeCode.TryParse(raw, out var id, out var secret))
            return new CashierBadgeCheck(CashierBadgeStatus.Malformed);
        var record = records.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
        if (record is null)
            return new CashierBadgeCheck(CashierBadgeStatus.Unknown);
        if (!CashierBadgeCode.HashEquals(record.Hash, CashierBadgeCode.HashSecret(id, secret)))
            return new CashierBadgeCheck(CashierBadgeStatus.WrongSecret);
        if (record.RevokedAt is not null)
            return new CashierBadgeCheck(CashierBadgeStatus.Revoked, record);
        if (record.ExpiresAt is { } exp && now.ToUnixTimeMilliseconds() >= exp)
            return new CashierBadgeCheck(CashierBadgeStatus.Expired, record);
        return new CashierBadgeCheck(CashierBadgeStatus.Ok, record);
    }

    /// <summary>Понятное сообщение кассиру на 5 языках.</summary>
    public static string Message(CashierBadgeStatus status, TimeSpan? wait = null) => status switch
    {
        CashierBadgeStatus.Malformed => Tr.T("QR-бейдж не прочитан целиком. Поднесите его к сканеру ещё раз.", "QR-бейдж толук окулган жок. Аны сканерге дагы бир жолу жакындатыңыз.",
            "The QR badge was not read completely. Scan it again.", "QR rozet tam okunamadı. Tekrar okutun.", "QR-beydj to'liq o'qilmadi. Uni skanerga yana bir bor yaqinlashtiring."),
        CashierBadgeStatus.Unknown => Tr.T("Этот QR-бейдж кассе неизвестен: его нет в списке бейджей магазина (отозван или касса ещё не получила список). Войдите паролем — касса обновит список.",
            "Бул QR-бейдж кассага белгисиз: ал дүкөндүн бейджтер тизмесинде жок (жокко чыгарылган же касса тизмени азырынча алган эмес). Сырсөз менен кириңиз — касса тизмени жаңыртат.",
            "This till does not know this QR badge: it is not in the shop's badge list (revoked, or the till has not received the list yet). Sign in with the password — the till will update the list.",
            "Bu kasa bu QR rozeti tanımıyor: mağazanın rozet listesinde yok (iptal edildi veya kasa listeyi henüz almadı). Şifreyle giriş yapın — kasa listeyi günceller.",
            "Bu QR-beydj kassaga noma'lum: u do'kon beydjlari ro'yxatida yo'q (bekor qilingan yoki kassa ro'yxatni hali olmagan). Parol bilan kiring — kassa ro'yxatni yangilaydi."),
        CashierBadgeStatus.WrongSecret => Tr.T("QR-бейдж недействителен. Обратитесь к владельцу за новым.", "QR-бейдж жараксыз. Жаңысы үчүн ээсине кайрылыңыз.",
            "The QR badge is not valid. Ask the owner for a new one.", "QR rozet geçersiz. Yenisi için sahibine başvurun.", "QR-beydj yaroqsiz. Yangisi uchun egasiga murojaat qiling."),
        CashierBadgeStatus.Revoked => Tr.T("Этот QR-бейдж отозван владельцем. Войдите паролем или попросите новый бейдж.", "Бул QR-бейджди ээси жокко чыгарган. Сырсөз менен кириңиз же жаңы бейдж сураңыз.",
            "This QR badge was revoked by the owner. Sign in with the password or ask for a new badge.", "Bu QR rozet sahibi tarafından iptal edildi. Şifreyle giriş yapın veya yeni rozet isteyin.",
            "Bu QR-beydjni egasi bekor qilgan. Parol bilan kiring yoki yangi beydj so'rang."),
        CashierBadgeStatus.Expired => Tr.T("Срок действия QR-бейджа истёк. Попросите у владельца новый.", "QR-бейджтин мөөнөтү бүттү. Ээсинен жаңысын сураңыз.",
            "The QR badge has expired. Ask the owner for a new one.", "QR rozetin süresi doldu. Sahibinden yenisini isteyin.", "QR-beydj muddati tugadi. Egasidan yangisini so'rang."),
        CashierBadgeStatus.LockedOut => WaitText(wait ?? TimeSpan.Zero),
        _ => "",
    };

    public static string WaitText(TimeSpan wait)
    {
        var s = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        var text = s >= 60 ? $"{(s + 59) / 60}" : $"{s}";
        return s >= 60
            ? Tr.T($"Слишком много неудачных попыток входа по бейджу. Подождите {text} мин.", $"Бейдж менен кирүүнүн ийгиликсиз аракеттери өтө көп. {text} мүн күтүңүз.",
                $"Too many failed badge sign-in attempts. Wait {text} min.", $"Çok fazla başarısız rozet girişi. {text} dk bekleyin.", $"Beydj bilan kirishda muvaffaqiyatsiz urinishlar juda ko'p. {text} daq kuting.")
            : Tr.T($"Слишком много неудачных попыток входа по бейджу. Подождите {text} с.", $"Бейдж менен кирүүнүн ийгиликсиз аракеттери өтө көп. {text} сек күтүңүз.",
                $"Too many failed badge sign-in attempts. Wait {text} s.", $"Çok fazla başarısız rozet girişi. {text} sn bekleyin.", $"Beydj bilan kirishda muvaffaqiyatsiz urinishlar juda ko'p. {text} soniya kuting.");
    }
}
