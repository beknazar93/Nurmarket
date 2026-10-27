using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-27, просьба владельца «добавь загрузку электронных накладных»: кнопка «Загрузить накладную» в Склад → Приёмка (рядом с «Товары поставщика» и «＋ поставщик»)
/// и перетаскивание файла (.xlsx, .csv, .xml) в окно. Вся логика — Dialogs/InvoiceImportFlow.cs; здесь только кнопка. Права — как у приёмки (InvoiceImportFlow.CanImport).</summary>
public partial class WarehouseWindow
{
    private void InitInvoiceImport()
    {
        ReceivingInvoiceImportButton.Content = Tr.T("Загрузить накладную", "Накладнойду жүктөө", "Load an invoice", "Fatura yükle", "Yuk xatini yuklash");
        ToolTip.SetTip(ReceivingInvoiceImportButton, Tr.T(
            "Электронная накладная поставщика: Excel (.xlsx), CSV или XML. Файл можно и перетащить в это окно. Строки попадут в приёмку, оприходуете вы сами.",
            "Жеткирүүчүнүн электрондук накладнойу: Excel (.xlsx), CSV же XML. Файлды ушул терезеге сүйрөп да койсо болот. Саптар кабыл алууга түшөт, кирешеге өзүңүз аласыз.",
            "A supplier's electronic invoice: Excel (.xlsx), CSV or XML. You can also drag the file into this window. Lines go to the receiving list; you post them yourself.",
            "Tedarikçinin elektronik faturası: Excel (.xlsx), CSV veya XML. Dosyayı bu pencereye sürükleyebilirsiniz. Satırlar mal kabul listesine gider, stoğa siz alırsınız.",
            "Yetkazib beruvchining elektron yuk xati: Excel (.xlsx), CSV yoki XML. Faylni shu oynaga sudrab ham tashlash mumkin. Qatorlar qabul ro'yxatiga tushadi, kirim qilishni o'zingiz bajarasiz."));
        InvoiceImportFlow.AttachDrop(this, () => ReferenceEquals(WarehouseTabs.SelectedItem, ReceivingTabItem), () => _viewModel);
    }

    private async void ReceivingInvoiceImport_Click(object? sender, RoutedEventArgs e)
    {
        ReceivingInvoiceImportButton.IsEnabled = false; // от двойного нажатия
        try
        {
            ReceivingGrid.CommitEdit();
            await InvoiceImportFlow.RunAsync(this, _viewModel).ConfigureAwait(true);
        }
        finally
        {
            ReceivingInvoiceImportButton.IsEnabled = true;
        }
    }
}
