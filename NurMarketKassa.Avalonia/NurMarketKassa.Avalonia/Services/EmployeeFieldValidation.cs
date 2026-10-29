using System.Linq;
using System.Text.RegularExpressions;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-28: проверка полей сотрудника до отправки на сервер (формы «Новый сотрудник» и «Данные сотрудника»).
/// Правила мягкие: сервер остаётся главным, мы лишь ловим опечатки (почта без @, телефон из букв).</summary>
internal static class EmployeeFieldValidation
{
    private static readonly Regex EmailRx = new(@"^[^@\s]+@[^@\s]+\.[^@\s]{2,}$", RegexOptions.Compiled);

    public static bool IsEmail(string? s) => !string.IsNullOrWhiteSpace(s) && s.Length <= 254 && EmailRx.IsMatch(s.Trim());

    /// <summary>Телефон: цифры, пробелы, + - ( ), всего 7–15 цифр.</summary>
    public static bool IsPhone(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return false;
        if (s.Any(c => !(char.IsDigit(c) || c is ' ' or '+' or '-' or '(' or ')')))
            return false;
        var digits = s.Count(char.IsDigit);
        return digits is >= 7 and <= 15;
    }

    public static string EmailError => Tr.T("Проверьте Email: он должен быть вида name@mail.com.", "Email текшериңиз: name@mail.com түрүндө болушу керек.",
        "Check the email: it must look like name@mail.com.", "E-postayı kontrol edin: name@mail.com biçiminde olmalı.", "Emailni tekshiring: name@mail.com ko'rinishida bo'lishi kerak.");

    public static string PhoneError => Tr.T("Проверьте телефон: только цифры, + и пробелы, от 7 до 15 цифр.", "Телефонду текшериңиз: цифралар, + жана боштук, 7–15 цифра.",
        "Check the phone: digits, + and spaces only, 7 to 15 digits.", "Telefonu kontrol edin: yalnızca rakam, + ve boşluk, 7–15 hane.", "Telefonni tekshiring: faqat raqam, + va bo'shliq, 7–15 raqam.");
}
