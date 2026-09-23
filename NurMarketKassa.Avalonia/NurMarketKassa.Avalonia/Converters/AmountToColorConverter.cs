using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace NurMarketKassa.AvaloniaHost.Converters;

/// <summary>Цвет суммы в отчётах «Финансы»/«Продажи»: минус — красный, остальное — обычный цвет
/// текста темы. Раньше здесь были жёстко заданные Colors.Red/Colors.Black, из-за чего в ТЁМНОЙ
/// теме каждая положительная сумма в этих отчётах печаталась чёрным по тёмному фону и читалась
/// только выделением. Берём кисти из ресурсов приложения, чтобы цвет следовал за темой.</summary>
public sealed class AmountToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is decimal amount && amount < 0m ? "BrushDanger" : "BrushText";
        if (Application.Current?.TryFindResource(key, out var brush) == true && brush is IBrush found)
            return found;

        // Ресурсы недоступны (дизайнер/превью) — нейтральный запасной вариант.
        return new SolidColorBrush(key == "BrushDanger" ? Colors.Red : Colors.Gray);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
