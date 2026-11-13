using System.Text;

namespace NurMarketKassa.Services;

/// <summary>2026-11-12 (ТЗ «Сфера Электроника», ЭЛ2): проверка IMEI (15 цифр, контрольная цифра по алгоритму Луна) и приведение
/// серийного номера к единому виду. Только данные и расчёт, без окон; в сфере «Электроника» их использует окно серийного номера.</summary>
public static class ImeiValidator
{
    /// <summary>Границы длины серийного номера (S/N) — короче 4 знаков это почти наверняка опечатка, длиннее 40 не бывает.</summary>
    public const int MinSerialLength = 4;
    public const int MaxSerialLength = 40;

    /// <summary>Номер как его вводят или сканируют: без пробелов, дефисов и слэшей-разделителей, заглавными. «49-015420 323751/8» → «490154203237518».</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Trim())
        {
            if (char.IsWhiteSpace(ch) || ch is '-' or '/' or ' ')
                continue;
            sb.Append(char.ToUpperInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>Только цифры.</summary>
    public static bool IsAllDigits(string value)
    {
        if (value.Length == 0)
            return false;
        foreach (var ch in value)
            if (ch is < '0' or > '9')
                return false;
        return true;
    }

    /// <summary>Верный IMEI: ровно 15 цифр и контрольная цифра сходится (алгоритм Луна). Пример верного: 490154203237518.</summary>
    public static bool IsValidImei(string? text)
    {
        var v = Normalize(text);
        return v.Length == 15 && IsAllDigits(v) && LuhnOk(v);
    }

    /// <summary>Контрольная сумма Луна для строки цифр.</summary>
    public static bool LuhnOk(string digits)
    {
        var sum = 0;
        var alt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (alt)
            {
                d *= 2;
                if (d > 9)
                    d -= 9;
            }
            sum += d;
            alt = !alt;
        }
        return sum % 10 == 0;
    }

    /// <summary>Результат проверки введённого номера.</summary>
    public enum Verdict
    {
        /// <summary>Подходит: IMEI с верной контрольной цифрой или серийный номер.</summary>
        Ok,
        /// <summary>Пусто.</summary>
        Empty,
        /// <summary>Слишком короткий или длинный, либо недопустимые знаки.</summary>
        BadFormat,
        /// <summary>Похоже на IMEI (14–16 цифр), но не 15 цифр или контрольная цифра не сошлась — скорее всего опечатка.</summary>
        BadImei,
    }

    /// <summary>Проверка номера. Только цифры длиной 14–16 считаются попыткой ввести IMEI — они обязаны быть верным IMEI;
    /// остальное — серийный номер (латиница, цифры, точка, подчёркивание), 4–40 знаков.</summary>
    public static Verdict Check(string? text, out string normalized)
    {
        normalized = Normalize(text);
        if (normalized.Length == 0)
            return Verdict.Empty;
        if (normalized.Length is < MinSerialLength or > MaxSerialLength)
            return Verdict.BadFormat;
        foreach (var ch in normalized)
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_'))
                return Verdict.BadFormat;
        if (IsAllDigits(normalized) && normalized.Length is >= 14 and <= 16)
            return IsValidImei(normalized) ? Verdict.Ok : Verdict.BadImei;
        return Verdict.Ok;
    }

    /// <summary>Подпись номера на чеке: «IMEI» для верного IMEI, иначе «S/N».</summary>
    public static string LabelFor(string? serial) => IsValidImei(serial) ? "IMEI" : "S/N";
}
