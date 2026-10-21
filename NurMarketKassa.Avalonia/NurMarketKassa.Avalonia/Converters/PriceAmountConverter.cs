using System.Globalization;
using Avalonia.Data.Converters;

namespace NurMarketKassa.AvaloniaHost.Converters;

public sealed class PriceAmountConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string priceLine || string.IsNullOrWhiteSpace(priceLine))
            return "—";

        const string suffix = " сом";
        var idx = priceLine.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
        var text = idx > 0 ? priceLine[..idx].Trim() : priceLine.Trim();
        // 2026-10-19, К1 (аудит 19.10, проблема 1): цена плитки — тем же видом, что суммы чека и у владельца:
        // «3 500,00» (MoneyText). PriceLine — данные («3500.00 сом», её разбирает LocalCartService.ParsePrice) — не
        // меняется; не число (буквы или «1,234.50» с двумя разделителями) — показываем как было.
        var digits = new string(text.Where(c => char.IsDigit(c) || c is '.' or ',' or '-').ToArray());
        if (digits.Length == 0 || (digits.Contains('.') && digits.Contains(',')) || text.Any(char.IsLetter))
            return text;
        return double.TryParse(digits.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var price)
            // 2026-10-20: ConverterParameter=som — с валютой («85,00 сом»; избранное «1С», окна F-групп и замен).
            ? NurMarketKassa.Services.MoneyText.Amount(price) + (Equals(parameter, "som") ? " " + NurMarketKassa.Services.MoneyText.Currency : "")
            : text;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
