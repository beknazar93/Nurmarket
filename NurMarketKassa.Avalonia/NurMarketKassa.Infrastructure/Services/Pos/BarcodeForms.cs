namespace NurMarketKassa.Services;

/// <summary>2026-10-03, живой случай (клиент palma): на упаковке UPC-A из 12 цифр («762497741537»), в каталоге
/// и в общей базе NurCRM тот же код как EAN-13 с ведущим нулём («0762497741537»). Сканер присылает 12 цифр —
/// точный поиск не находил ни товар на складе, ни название в общей базе. Это одна и та же запись кода.</summary>
public static class BarcodeForms
{
    /// <summary>Вторая запись того же кода: 12 цифр → с нулём впереди, 13 цифр с нулём впереди → без него;
    /// иначе null.</summary>
    public static string? Alternate(string? code)
    {
        var key = code?.Trim() ?? "";
        if (key.Length == 12 && key.All(char.IsDigit))
            return "0" + key;
        if (key.Length == 13 && key[0] == '0' && key.All(char.IsDigit))
            return key[1..];
        return null;
    }
}
