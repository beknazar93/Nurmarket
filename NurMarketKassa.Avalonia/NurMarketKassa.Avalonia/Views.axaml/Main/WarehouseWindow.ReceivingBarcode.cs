using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-10 (владелец: «Генерация штрихкода как в вебе!»): «Сгенерировать штрихкод» у нового товара без штрихкода в «Склад → Приёмка».
/// Тот же генератор, что «Сгенерировать» в окне товара (BarcodeGenerator: EAN-13, первая цифра не 0 и не 2 — префиксы 20–29 касса читает как весовые),
/// код свободен в каталоге, индексе размеров и среди всех строк приёмки. Ручной ввод штрихкода остаётся. Этикетку можно сразу напечатать кнопкой «Этикетки на приход».</summary>
public partial class WarehouseWindow
{
    public static string GenerateBarcodeText => TR("Сгенерировать штрихкод", "Штрихкод түзүү", "Generate barcode", "Barkod oluştur", "Shtrix-kod yaratish");

    public static string GenerateBarcodeHintText => TR("Сгенерировать свой штрихкод EAN-13 для нового товара (как в окне товара и на сайте). Код не повторяет товары каталога и строки этой приёмки. Этикетку — кнопкой «Этикетки на приход».",
        "Жаңы товар үчүн өз EAN-13 штрихкодун түзүү (товар терезесиндегидей жана сайттагыдай). Код каталогдогу товарларды жана бул кабыл алуунун саптарын кайталабайт. Этикетка — «Кирешеге этикеткалар» баскычы менен.",
        "Generate an own EAN-13 barcode for the new product (as in the product window and on the website). The code does not repeat catalog products or lines of this receipt. Print the label with “Labels for this receipt”.",
        "Yeni ürün için kendi EAN-13 barkodunu oluştur (ürün penceresindeki ve sitedeki gibi). Kod katalogdaki ürünleri ve bu girişin satırlarını tekrarlamaz. Etiket — «Bu giriş için etiketler» düğmesiyle.",
        "Yangi mahsulot uchun o'z EAN-13 shtrix-kodini yaratish (mahsulot oynasidagi va saytdagidek). Kod katalogdagi mahsulotlar va bu qabul qatorlarini takrorlamaydi. Yorliq — «Kirim uchun yorliqlar» tugmasi bilan.");

    private void ReceivingGenerateBarcode_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Control)?.DataContext is not ReceivingLineVm line)
            return;
        if (!BarcodeGenerator.GenerateForReceivingLine(line, _viewModel.ReceivingLines))
            return;
        PosLogger.Log($"Приёмка: новому товару «{line.ProductName}» сгенерирован штрихкод {line.Barcode}.", "STOCK");
        _viewModel.ShowReceivingInfo(TR($"Штрихкод {line.Barcode} сгенерирован. Этикетку можно напечатать кнопкой «Этикетки на приход».",
            $"{line.Barcode} штрихкоду түзүлдү. Этикетканы «Кирешеге этикеткалар» баскычы менен басып чыгарса болот.",
            $"Barcode {line.Barcode} generated. Print the label with “Labels for this receipt”.",
            $"{line.Barcode} barkodu oluşturuldu. Etiketi «Bu giriş için etiketler» düğmesiyle yazdırabilirsiniz.",
            $"{line.Barcode} shtrix-kodi yaratildi. Yorliqni «Kirim uchun yorliqlar» tugmasi bilan chop etish mumkin."));
        RefreshReceivingSummary();
    }
}
