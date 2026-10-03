namespace NurMarketKassa.Services;

/// <summary>2026-10-04, ТЗ разработчика приложения NurCRM: QR клиента из бонусного приложения («Покажите
/// этот код на кассе, чтобы копить баллы») — строка «NURCRM» и 12 цифр телефона (996 + 9 цифр), например
/// «NURCRM996700123456» → +996700123456. Больше в коде ничего нет. Сканер печатает её как клавиатура.
///
/// Распознаём ТОЛЬКО строки с префиксом NURCRM (без учёта регистра) — обычные и весовые штрихкоды сюда не
/// попадают, догадок «12 цифр 996… — это телефон» нет. Если код набран в русской/кыргызской раскладке (сканер
/// шлёт клавиши, а поле ввода печатает буквы раскладки), префикс приходит как «ТГКСКЬ» — переводим раскладку
/// ЙЦУКЕН → QWERTY по клавишам (N=Т, U=Г, R=К, C=С, M=Ь); цифры в обеих раскладках одинаковые.
///
/// Разбор возвращает вид кода (<see cref="ClientQrKind"/>), чтобы добавить новые форматы, не трогая кассу:
/// следующий по ТЗ — «NURCRMT&lt;токен&gt;» (одноразовый код; касса обменяет его на клиента запросом к серверу,
/// адрес вроде GET …/clients/by-qr-token/?t=… появится позже).</summary>
public static class ClientQrCode
{
    public const string Prefix = "NURCRM";

    /// <summary>Будущий формат: «NURCRMT» + одноразовый токен.</summary>
    public const string TokenPrefix = "NURCRMT";

    /// <param name="Kind">Вид кода.</param>
    /// <param name="Phone">Для <see cref="ClientQrKind.Phone"/>: «+996» и 9 цифр.</param>
    /// <param name="National">Для <see cref="ClientQrKind.Phone"/>: 9 цифр номера без кода страны.</param>
    /// <param name="Token">Для <see cref="ClientQrKind.Token"/>: токен после «NURCRMT».</param>
    public sealed record Result(ClientQrKind Kind, string? Phone = null, string? National = null, string? Token = null);

    /// <returns>false — это не QR клиента NurCRM (обычный штрихкод).</returns>
    public static bool TryParse(string? raw, out Result? result)
    {
        result = null;
        var text = raw?.Trim() ?? "";
        if (text.Length < Prefix.Length
            || !string.Equals(ToLatinLayout(text[..Prefix.Length]), Prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = text[Prefix.Length..].Trim();

        // «NURCRMT…» — одноразовый токен (формат на будущее; обмен на клиента появится с адресом сервера).
        if (rest.Length > 1 && char.ToUpperInvariant(ToLatinLayout(rest[0])) == 'T')
        {
            result = new Result(ClientQrKind.Token, Token: ToLatinLayout(rest[1..]));
            return true;
        }

        if (rest.Length == 12 && rest.All(char.IsAsciiDigit) && rest.StartsWith("996", StringComparison.Ordinal))
        {
            var national = rest[3..];
            result = new Result(ClientQrKind.Phone, Phone: "+996" + national, National: national);
            return true;
        }

        result = new Result(ClientQrKind.Invalid);
        return true;
    }

    /// <summary>9 цифр номера без кода страны из телефона в любой записи («+996 700 123 456»,
    /// «996700123456», «0700123456», «700123456», «0700 12-34-56») — так сравниваются телефоны клиентов
    /// NurCRM, записанные как попало; null — не номер Кыргызстана.</summary>
    public static string? NationalDigits(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return null;

        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 12 && digits.StartsWith("996", StringComparison.Ordinal))
            return digits[3..];
        if (digits.Length == 13 && digits.StartsWith("9960", StringComparison.Ordinal))
            return digits[4..];
        if (digits.Length == 10 && digits[0] == '0')
            return digits[1..];
        return digits.Length == 9 ? digits : null;
    }

    /// <summary>Номер для журнала: «+996700***456» — полный номер клиента в журнал не пишем.</summary>
    public static string MaskPhone(string? national) =>
        national is { Length: 9 } ? $"+996{national[..3]}***{national[^3..]}" : "+996***";

    // Клавиши ЙЦУКЕН → QWERTY (русская и кыргызская раскладки совпадают на этих клавишах).
    private const string CyrillicKeys = "йцукенгшщзхъфывапролджэячсмитьбюё";
    private const string LatinKeys = "qwertyuiop[]asdfghjkl;'zxcvbnm,.`";

    private static char ToLatinLayout(char c)
    {
        var index = CyrillicKeys.IndexOf(char.ToLowerInvariant(c));
        if (index < 0)
            return c;
        var latin = LatinKeys[index];
        return char.IsUpper(c) ? char.ToUpperInvariant(latin) : latin;
    }

    private static string ToLatinLayout(string text) =>
        string.Concat(text.Select(ToLatinLayout));
}

/// <summary>Вид QR клиента NurCRM.</summary>
public enum ClientQrKind
{
    /// <summary>«NURCRM» + 12 цифр телефона.</summary>
    Phone,

    /// <summary>«NURCRMT» + одноразовый токен — на будущее, касса его пока не обменивает.</summary>
    Token,

    /// <summary>Префикс NURCRM есть, а дальше не 12 цифр «996…» — кассиру понятное сообщение.</summary>
    Invalid,
}
