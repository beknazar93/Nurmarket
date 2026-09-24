using System.Globalization;
using Avalonia.Data.Converters;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Converters;

/// <summary>Localizes the catalog tile's type badge — the text itself can't live in
/// NurMarketKassa.Core (CatalogProductTileVm), which cannot reference UserPreferences/Language
/// (Infrastructure). 2026-09-08: badge now distinguishes Комплект (IsBundle) and
/// "Штучный + Поштучно" (piece with a pack-breakdown option), not just Штучный/Весовой — по
/// просьбе владельца ("Эссе манго" should read "штучный + Поштучно", "тест штучные" should read
/// "Комплект"). Bound to the whole CatalogProductTileVm (not just IsWeighted) so it can see all
/// three flags together.</summary>
public sealed class TypeBadgeTextConverter : IValueConverter
{
    public static readonly TypeBadgeTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not CatalogProductTileVm vm)
            return "";

        if (vm.IsService)
            return Tr.T("Услуга", "Кызмат", "Service", "Hizmet", "Xizmat");
        if (vm.IsBundle)
            return Tr.T("Комплект", "Комплект", "Kit", "Set", "To'plam");
        if (vm.IsWeighted)
            return Tr.T("Весовой", "Салмактуу", "By weight", "Tartılan", "Tortiladigan");
        if (vm.HasPieceOption)
            return Tr.T("Штучный + Поштучно", "Даана + Пакеттен даана", "Piece + from pack", "Adet + Paketten adet", "Dona + Paketdan dona");
        return Tr.T("Штучный", "Даана", "Piece", "Adet", "Dona");
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
