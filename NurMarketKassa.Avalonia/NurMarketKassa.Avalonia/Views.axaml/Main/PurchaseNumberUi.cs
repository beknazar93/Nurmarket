using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-30 (нумерация закупок): общие мелочи показа номера документа — копирование по клику/правой кнопкой и тексты.</summary>
internal static class PurchaseNumberUi
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public static string CopyHint => T("Нажмите, чтобы скопировать номер", "Номерди көчүрүү үчүн басыңыз", "Click to copy the number", "Numarayı kopyalamak için tıklayın", "Raqamni nusxalash uchun bosing");

    public static string CopiedText => T("Номер скопирован", "Номер көчүрүлдү", "Number copied", "Numara kopyalandı", "Raqam nusxalandi");

    public static async Task CopyAsync(Visual host, string text)
    {
        try
        {
            if (TopLevel.GetTopLevel(host)?.Clipboard is { } clipboard && text.Length > 0)
                await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки: номер документа не скопирован ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Контекстное меню «Копировать номер» на строке списка (щелчок по строке открывает документ, поэтому копирование — правой кнопкой).</summary>
    public static void AttachCopyMenu(Control row, string number)
    {
        if (number.Length == 0)
            return;
        var item = new MenuItem { Header = T("Копировать номер ", "Номерди көчүрүү ", "Copy number ", "Numarayı kopyala ", "Raqamni nusxalash ") + number };
        item.Click += async (_, _) => await CopyAsync(row, number).ConfigureAwait(true);
        row.ContextMenu = new ContextMenu { ItemsSource = new[] { item } };
    }

    /// <summary>Подпись документа: номер «ПР-2026-000123» и, если есть, номер накладной поставщика отдельно.</summary>
    public static string SupplierDocText(string? number, DateTime? date) =>
        string.IsNullOrWhiteSpace(number) ? "" : T("накладная поставщика № ", "жеткирүүчүнүн накладнойу № ", "supplier invoice № ", "tedarikçi faturası № ", "yetkazib beruvchi yuk xati № ")
            + number!.Trim() + (date is { } d ? " " + T("от ", "", "of ", "", "") + d.ToString("dd.MM.yyyy") : "");
}
