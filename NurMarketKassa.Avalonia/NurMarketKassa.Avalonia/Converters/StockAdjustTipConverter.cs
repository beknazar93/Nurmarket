using System.Globalization;
using Avalonia.Data.Converters;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Converters;

/// <summary>2026-10-06, редизайн склада: подсказка кнопки «±» в строке товара (на 5 языках, без новых ключей в Strings.*).</summary>
public sealed class StockAdjustTipConverter : IValueConverter
{
    public static readonly StockAdjustTipConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Tr.T("Изменить остаток: приход, списание или точное количество", "Калдыкты өзгөртүү: кириш, эсептен чыгаруу же так сан",
            "Change stock: receipt, write-off or exact quantity", "Stoğu değiştir: giriş, düşüm veya tam miktar",
            "Qoldiqni o'zgartirish: kirim, hisobdan chiqarish yoki aniq miqdor");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
