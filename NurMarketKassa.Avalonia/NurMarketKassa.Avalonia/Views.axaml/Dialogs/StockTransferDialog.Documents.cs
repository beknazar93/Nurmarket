using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-10-31, просьба владельца (вкладка «Документы» карточки перемещения): «автоматическое создание документов с примерами как в
/// накладной КР». Кнопка «Создать документ» сама формирует накладную на внутреннее перемещение, акт приёма-передачи или транспортный
/// документ из данных перемещения (откуда/куда, ответственный, перевозчик, трек-номер, вес, состав) и прикрепляет файл (PDF) к перемещению
/// тем же способом, что «Прикрепить» (копия в папку данных кассы, вид и дата в списке). Печать, Word и Excel — по снимку данных, который
/// лежит рядом с файлом. При «Отправить» накладная создаётся сама (флажок, по умолчанию включён; акт и транспортный — по второму флажку,
/// по умолчанию выключен). Деньги, остатки и сам процесс перемещения не трогаются: документ только читает данные.</summary>
public partial class StockTransferDialog
{
    private void InitDocumentUi()
    {
        CreateDocButton.Content = T("Создать документ", "Документ түзүү", "Create document", "Belge oluştur", "Hujjat yaratish");
        PrintDocButton.Content = T("Печать", "Басып чыгаруу", "Print", "Yazdır", "Chop etish");
        AutoWaybillCheck.Content = T("Создавать накладную автоматически при отправке", "Жөнөтүүдө накладнойду автоматтык түзүү",
            "Create the waybill automatically on dispatch", "Gönderimde irsaliyeyi otomatik oluştur", "Jo'natishda yuk xatini avtomatik yaratish");
        AutoOtherCheck.Content = T("Акт и транспортный документ — тоже", "Акт жана транспорттук документ да",
            "Also the handover certificate and transport document", "Teslim tutanağı ve taşıma belgesi de", "Qabul-topshirish dalolatnomasi va transport hujjati ham");
        AutoWaybillCheck.IsChecked = UserPreferences.Instance.TransferAutoWaybill;
        AutoOtherCheck.IsChecked = UserPreferences.Instance.TransferAutoOther;
    }

    private void AutoDocs_Click(object? sender, RoutedEventArgs e)
    {
        var prefs = UserPreferences.Instance;
        prefs.TransferAutoWaybill = AutoWaybillCheck.IsChecked == true;
        prefs.TransferAutoOther = AutoOtherCheck.IsChecked == true;
        prefs.SaveToDisk();
    }

    private void SayDoc(string? text)
    {
        DocStatusText.Text = text ?? "";
        DocStatusText.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    /// <summary>Закупочная цена строки — из каталога кассы (по id товара, иначе по штрихкоду). Только у ролей с правом видеть закупку.</summary>
    private static Func<StockTransferService.TransferItem, double>? PurchasePriceLookup()
    {
        if (!WarehouseWindow.RevisionShowPurchase)
            return null;
        var products = CatalogCacheService.Products;
        return item =>
        {
            var product = item.ProductId is { Length: > 0 } id ? products.FirstOrDefault(p => p.Id == id) : null;
            if (product is null && !string.IsNullOrWhiteSpace(item.Barcode))
                product = products.FirstOrDefault(p => string.Equals(p.Barcode, item.Barcode, System.StringComparison.OrdinalIgnoreCase));
            return product?.PurchasePrice ?? 0;
        };
    }

    private TransferDocData? BuildDocData(TransferDocKind kind)
    {
        // Свежие данные из базы: поля шапки сохраняются, как только из них уходит курсор.
        var transfer = StockTransferService.Instance.LoadTransfers(limit: 1000).FirstOrDefault(t => t.Id == _transferId);
        if (transfer is null)
            return null;
        var items = StockTransferService.Instance.LoadItems(_transferId);
        return TransferDocuments.Build(kind, transfer, items, PurchasePriceLookup(),
            App.GetRequiredService<NurMarketKassa.Ui.Shared.IAppSession>().CurrentUserDisplayName);
    }

    private async void CreateDoc_Click(object? sender, RoutedEventArgs e)
    {
        ShowWarning(null);
        var tag = (FileKindBox.SelectedItem as ComboBoxItem)?.Tag as string;
        if (TransferDocuments.KindFromTag(tag) is not { } kind)
        {
            SayDoc(T("Этот вид можно только прикрепить — нажмите «Прикрепить».", "Бул түрдү тиркөөгө гана болот — «Тиркөө» баскычын басыңыз.",
                "This kind can only be attached — use “Attach”.", "Bu tür yalnızca eklenebilir — «Ekle» düğmesine basın.", "Bu turni faqat biriktirish mumkin — «Biriktirish» tugmasini bosing."));
            return;
        }

        // 2026-10-31 (приёмка 1.17.70): чтение базы вне try в async void уходило в общий обработчик ошибок — теперь в журнал и строку статуса.
        TransferDocData? data;
        try
        {
            data = BuildDocData(kind);
        }
        catch (System.Exception ex)
        {
            PosLogger.Log($"Перемещение {_transfer?.Number}: данные документа не прочитаны ({ex})", "WARNING");
            SayDoc(T("Документ не создан: ", "Документ түзүлгөн жок: ", "The document was not created: ", "Belge oluşturulamadı: ", "Hujjat yaratilmadi: ") + ex.Message);
            return;
        }
        if (data is null)
            return;
        if (data.Lines.Count == 0)
        {
            ShowWarning(T("Добавьте товары в перемещение — документ создаётся из его состава.", "Жылдырууга товар кошуңуз — документ анын курамынан түзүлөт.",
                "Add products to the transfer — the document is built from its contents.", "Transfere ürün ekleyin — belge içeriğinden oluşturulur.", "Ko'chirishga mahsulot qo'shing — hujjat uning tarkibidan yaratiladi."));
            return;
        }

        CreateDocButton.IsEnabled = false;
        try
        {
            var path = await System.Threading.Tasks.Task.Run(() => TransferDocuments.CreateAndAttach(_transferId, data)).ConfigureAwait(true);
            Reload();
            if (FilesGrid.ItemsSource is System.Collections.Generic.List<FileRow> rows)
                FilesGrid.SelectedItem = rows.LastOrDefault(r => r.FilePath == path);
            SayDoc(T("Документ создан и прикреплён: ", "Документ түзүлүп, тиркелди: ", "Document created and attached: ", "Belge oluşturuldu ve eklendi: ", "Hujjat yaratildi va biriktirildi: ")
                   + TransferDocuments.Title(kind, data.Lang) + " " + data.Number);
            PurchaseDocs.OpenFile(path);
        }
        catch (System.Exception ex)
        {
            PosLogger.Log($"Перемещение {_transfer?.Number}: документ не создан ({ex})", "WARNING");
            SayDoc(T("Документ не создан: ", "Документ түзүлгөн жок: ", "The document was not created: ", "Belge oluşturulamadı: ", "Hujjat yaratilmadi: ") + ex.Message);
        }
        finally
        {
            CreateDocButton.IsEnabled = true;
        }
    }

    /// <summary>«Отправить» — перемещение ушло в путь: накладная (и по второму флажку акт и транспортный документ) создаются сами и
    /// прикладываются к документам. Любая ошибка — только в журнал: отправка перемещения от документов не зависит.</summary>
    private async System.Threading.Tasks.Task AutoDocumentsOnShipAsync()
    {
        try
        {
            var prefs = UserPreferences.Instance;
            var kinds = new System.Collections.Generic.List<TransferDocKind>();
            if (prefs.TransferAutoWaybill)
                kinds.Add(TransferDocKind.Waybill);
            if (prefs.TransferAutoOther)
            {
                kinds.Add(TransferDocKind.Act);
                kinds.Add(TransferDocKind.Transport);
            }
            if (kinds.Count == 0)
                return;

            var all = kinds.Select(BuildDocData).Where(d => d is { Lines.Count: > 0 }).Select(d => d!).ToList();
            if (all.Count == 0)
                return;
            await System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var data in all)
                    TransferDocuments.CreateAndAttach(_transferId, data);
            }).ConfigureAwait(true);
            if (IsVisible)
            {
                Reload();
                SayDoc(T("При отправке созданы документы: ", "Жөнөтүүдө документтер түзүлдү: ", "Documents created on dispatch: ", "Gönderimde belgeler oluşturuldu: ", "Jo'natishda hujjatlar yaratildi: ")
                       + string.Join(", ", all.Select(d => d.Number)));
            }
        }
        catch (System.Exception ex)
        {
            PosLogger.Log($"Перемещение {_transfer?.Number}: документы при отправке не созданы ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Снимок данных выбранного в списке файла: есть только у документов, созданных кнопкой «Создать документ».</summary>
    private TransferDocData? SelectedDocData()
    {
        if (FilesGrid.SelectedItem is not FileRow row || !File.Exists(row.FilePath))
        {
            SayDoc(T("Выберите в списке созданный документ.", "Тизмеден түзүлгөн документти тандаңыз.", "Select a created document in the list.", "Listeden oluşturulmuş bir belge seçin.", "Ro'yxatdan yaratilgan hujjatni tanlang."));
            return null;
        }

        var data = TransferDocuments.LoadSnapshot(row.FilePath);
        if (data is null)
            SayDoc(T("Печать, Word и Excel доступны для документов, созданных кнопкой «Создать документ»; прикреплённый файл откройте кнопкой «Открыть».",
                "Басып чыгаруу, Word жана Excel «Документ түзүү» баскычы менен түзүлгөн документтер үчүн; тиркелген файлды «Ачуу» менен ачыңыз.",
                "Print, Word and Excel work for documents made with “Create document”; open an attached file with “Open”.",
                "Yazdırma, Word ve Excel «Belge oluştur» ile hazırlanan belgeler içindir; eklenen dosyayı «Aç» ile açın.",
                "Chop etish, Word va Excel «Hujjat yaratish» bilan yaratilgan hujjatlar uchun; biriktirilgan faylni «Ochish» bilan oching."));
        return data;
    }

    private async void PrintDoc_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedDocData() is not { } data)
            return;
        try
        {
            var printer = ClientDocPrint.DefaultPrinter();
            if (string.IsNullOrWhiteSpace(printer))
            {
                SayDoc(T("Принтер по умолчанию не найден.", "Демейки принтер табылган жок.", "No default printer found.", "Varsayılan yazıcı bulunamadı.", "Standart printer topilmadi."));
                return;
            }

            var error = await System.Threading.Tasks.Task.Run(() =>
                ClientDocPrint.Print(printer, TransferDocuments.Title(data.Kind, data.Lang) + " " + data.Number, TransferDocumentRenderer.PageImages(data, 200))).ConfigureAwait(true);
            SayDoc(error ?? T("Отправлено на принтер.", "Принтерге жөнөтүлдү.", "Sent to the printer.", "Yazıcıya gönderildi.", "Printerga yuborildi."));
        }
        catch (System.Exception ex)
        {
            PosLogger.Log($"Перемещение {_transfer?.Number}: печать документа не удалась ({ex.Message}).", "WARNING");
            SayDoc(ex.Message);
        }
    }

    private async void DocWord_Click(object? sender, RoutedEventArgs e) => await ExportDocAsync(toWord: true);

    private async void DocExcel_Click(object? sender, RoutedEventArgs e) => await ExportDocAsync(toWord: false);

    private async System.Threading.Tasks.Task ExportDocAsync(bool toWord)
    {
        if (SelectedDocData() is not { } data)
            return;

        var extension = toWord ? "docx" : "xlsx";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = toWord
                ? T("Сохранить документ в Word", "Документти Word форматында сактоо", "Save the document to Word", "Belgeyi Word olarak kaydet", "Hujjatni Word formatida saqlash")
                : T("Сохранить документ в Excel", "Документти Excel форматында сактоо", "Save the document to Excel", "Belgeyi Excel olarak kaydet", "Hujjatni Excel formatida saqlash"),
            SuggestedFileName = TransferDocuments.FileName(data, extension),
            FileTypeChoices = [new FilePickerFileType(toWord ? "Word" : "Excel") { Patterns = [$"*.{extension}"] }],
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                if (toWord)
                    TransferDocumentOffice.SaveWord(data, path!);
                else
                    TransferDocumentOffice.SaveExcel(data, path!);
            }).ConfigureAwait(true);
            SayDoc(T("Файл сохранён: ", "Файл сакталды: ", "File saved: ", "Dosya kaydedildi: ", "Fayl saqlandi: ") + path);
        }
        catch (System.Exception ex)
        {
            PosLogger.Log($"Перемещение {_transfer?.Number}: выгрузка документа не удалась ({ex})", "WARNING");
            SayDoc(T("Файл не сохранён: ", "Файл сакталган жок: ", "File not saved: ", "Dosya kaydedilemedi: ", "Fayl saqlanmadi: ") + ex.Message);
        }
    }
}
