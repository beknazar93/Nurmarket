using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-05, жалоба владельца («в десктопе возврат поставщику — нет возможности распечатки накладной»): кнопки «Печать накладной»,
/// «PDF», «Word», «Excel» у возврата поставщику — одни и те же в окне «Возвраты поставщикам», в карточке поставщика и после оформления возврата.
/// Документ строится по ответу сервера (SupplierReturnDocuments): номер сервера, поставщик, приход-основание, состав; ничего не пишется на сервер.
/// Печать — на принтер по умолчанию (A4, как документы перемещения); нет принтера или это не Windows — PDF (папка «Документы\NurMarket\Закупки»
/// и открыть; на Android — выбор файла). Сохранение — окном выбора файла, после него файл открывается (кроме Android).</summary>
internal static class SupplierReturnDocUi
{
    internal enum Format { Print, Pdf, Word, Excel }

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Панель кнопок документа возврата со строкой состояния под ними. <paramref name="info"/> — возврат из ответа сервера,
    /// <paramref name="supplier"/> — карточка поставщика, если она уже есть (иначе её запросит сам документ).</summary>
    public static Control Bar(Control host, Func<SupplierReturnInfo> info, SupplierInfo? supplier = null)
    {
        var status = Status();
        var buttons = new List<Button>();
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (format, text) in new[]
                 {
                     (Format.Print, T("Печать накладной", "Накладнойду басып чыгаруу", "Print waybill", "İrsaliyeyi yazdır", "Yuk xatini chop etish")),
                     (Format.Pdf, "PDF"),
                     (Format.Word, "Word"),
                     (Format.Excel, "Excel"),
                 })
        {
            var button = Small(text);
            button.Margin = new Thickness(0, 0, 8, 6);
            button.Click += async (_, _) =>
            {
                foreach (var b in buttons)
                    b.IsEnabled = false;
                try
                {
                    await RunAsync(host, format, () => Task.FromResult(info()), supplier, text => Say(status, text)).ConfigureAwait(true);
                }
                finally
                {
                    foreach (var b in buttons)
                        b.IsEnabled = true;
                }
            };
            buttons.Add(button);
            row.Children.Add(button);
        }
        return new StackPanel { Spacing = 2, Children = { row, status } };
    }

    public static TextBlock Status()
    {
        var t = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
        return t;
    }

    public static void Say(TextBlock status, string? text)
    {
        status.Text = text ?? "";
        status.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    private static Button Small(string text)
    {
        var b = new Button
        {
            Content = text, Height = 34, MinHeight = 34, Padding = new Thickness(12, 0), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        b.Classes.Add("sec-outline");
        Avalonia.Automation.AutomationProperties.SetName(b, text);
        return b;
    }

    private static string Folder()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NurMarket", "Закупки");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Подготовить документ и выполнить действие. <paramref name="resolve"/> отдаёт возврат (для только что оформленного — найденный
    /// на сервере или собранный из окна); <paramref name="say"/> получает текст состояния («Готовлю…», «Отправлено на принтер.», ошибка).</summary>
    public static async Task RunAsync(Control host, Format format, Func<Task<SupplierReturnInfo>> resolve, SupplierInfo? supplier, Action<string> say)
    {
        SupplierReturnDocData data;
        try
        {
            say(T("Готовлю накладную…", "Накладной даярдалууда…", "Preparing the waybill…", "İrsaliye hazırlanıyor…", "Yuk xati tayyorlanmoqda…"));
            var list = await resolve().ConfigureAwait(true);
            var prepared = await PrepareAsync(list, supplier).ConfigureAwait(true);
            if (prepared.Info.Lines.Count == 0)
            {
                say(T("Не удалось получить состав возврата — проверьте связь с сервером и нажмите ещё раз.",
                    "Кайтаруунун курамын алуу мүмкүн болгон жок — сервер менен байланышты текшерип, кайра басыңыз.",
                    "Could not get the return contents — check the connection to the server and try again.",
                    "İade içeriği alınamadı — sunucu bağlantısını kontrol edip tekrar deneyin.",
                    "Qaytarish tarkibini olib bo'lmadi — server bilan aloqani tekshirib, qayta bosing."));
                return;
            }
            data = SupplierReturnDocuments.Build(prepared.Info, prepared.Supplier, App.CurrentUserDisplayName, prepared.ReceiptNumber);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Накладная возврата поставщику: данные не собраны ({ex}).", "WARNING");
            say(T("Накладная не подготовлена: ", "Накладной даярдалган жок: ", "The waybill was not prepared: ", "İrsaliye hazırlanamadı: ", "Yuk xati tayyorlanmadi: ") + ex.Message);
            return;
        }

        try
        {
            if (format == Format.Print)
                await PrintAsync(host, data, say).ConfigureAwait(true);
            else
                await SaveAsync(host, data, format, say).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Накладная возврата {data.Number}: {format} не удалась ({ex})", "WARNING");
            say((format == Format.Print
                    ? T("Не удалось напечатать: ", "Басып чыгаруу мүмкүн болгон жок: ", "Could not print: ", "Yazdırılamadı: ", "Chop etib bo'lmadi: ")
                    : T("Файл не сохранён: ", "Файл сакталган жок: ", "File not saved: ", "Dosya kaydedilemedi: ", "Fayl saqlanmadi: ")) + ex.Message);
        }
    }

    /// <summary>Дополнить возврат из списка данными сервера: карточка возврата (номер, артикулы, склад), карточка поставщика (ИНН, адрес, телефон),
    /// номер прихода-основания. Каждый запрос — отдельно и с общим сроком 12 секунд: нет связи — документ строится из того, что уже есть
    /// (строка списка и память последнего просмотра), без исключения.</summary>
    private static async Task<(SupplierReturnInfo Info, SupplierInfo? Supplier, string? ReceiptNumber)> PrepareAsync(SupplierReturnInfo listed, SupplierInfo? supplier)
    {
        var svc = new SupplierApiService(App.GetRequiredService<NurMarketApiClient>());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var info = listed;
        var cardTask = Safe(listed.Id.Length > 0 ? svc.GetReturnAsync(listed.Id, cts.Token) : Task.FromResult<SupplierReturnInfo?>(null)!, "карточка возврата");
        var supplierTask = supplier is not null || listed.SupplierId.Length == 0
            ? Task.FromResult(supplier)
            : Safe(svc.GetAsync(listed.SupplierId, cts.Token), "карточка поставщика");
        var receiptTask = listed.ReceiptId.Length > 0 && listed.ReceiptNumber.Length == 0
            ? Safe(svc.GetReceiptAsync(listed.ReceiptId, cts.Token), "приход-основание")
            : Task.FromResult<SupplierReceipt?>(null);
        await Task.WhenAll(cardTask, supplierTask, receiptTask).ConfigureAwait(true);

        if (cardTask.Result is { } card)
            info = Merge(listed, card);
        string? receiptNumber = receiptTask.Result is { } receipt ? DocumentNumbering.ReceiptText(receipt) : null;
        return (info, supplierTask.Result, receiptNumber);
    }

    private static async Task<T?> Safe<T>(Task<T?> task, string what) where T : class
    {
        try
        {
            return await task.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Накладная возврата поставщику: {what} не получена ({ex.Message}) — берём из списка.", "INFO");
            return null;
        }
    }

    private static string F(string a, string b) => a.Length > 0 ? a : b;

    /// <summary>Карточка сервера главнее строки списка; чего в карточке нет — берём из списка.</summary>
    private static SupplierReturnInfo Merge(SupplierReturnInfo list, SupplierReturnInfo card) => card with
    {
        Id = F(card.Id, list.Id), SupplierId = F(card.SupplierId, list.SupplierId), SupplierName = F(card.SupplierName, list.SupplierName),
        ReceiptId = F(card.ReceiptId, list.ReceiptId), CreatedAt = card.CreatedAt ?? list.CreatedAt, Reason = F(card.Reason, list.Reason),
        Compensation = F(card.Compensation, list.Compensation), Total = card.Total > 0 ? card.Total : list.Total, CreatedBy = F(card.CreatedBy, list.CreatedBy),
        Comment = F(card.Comment, list.Comment), Lines = card.Lines.Count > 0 ? card.Lines : list.Lines,
        NumberDisplay = F(card.NumberDisplay, list.NumberDisplay), ReceiptNumber = F(card.ReceiptNumber, list.ReceiptNumber), Warehouse = F(card.Warehouse, list.Warehouse),
    };

    private static async Task PrintAsync(Control host, SupplierReturnDocData data, Action<string> say)
    {
        // Windows с принтером — сразу на принтер; иначе PDF (печатать можно из программы просмотра).
        string? printer = null;
        if (OperatingSystem.IsWindows())
            printer = ClientDocPrint.DefaultPrinter();
        if (!string.IsNullOrWhiteSpace(printer) && OperatingSystem.IsWindows())
        {
            var name = printer!;
            var error = await Task.Run(() => OperatingSystem.IsWindows()
                ? ClientDocPrint.Print(name, SupplierReturnDocuments.PrintName(data), SupplierReturnDocumentRenderer.PageImages(data, 200))
                : null).ConfigureAwait(true);
            if (error is null)
                PosLogger.Log($"Накладная возврата {data.Number}: отправлена на принтер «{name}» ({data.Lines.Count} поз.).", "INFO");
            say(error ?? T("Отправлено на принтер: ", "Принтерге жөнөтүлдү: ", "Sent to the printer: ", "Yazıcıya gönderildi: ", "Printerga yuborildi: ") + name);
            return;
        }

        if (OperatingSystem.IsAndroid())
        {
            await SaveAsync(host, data, Format.Pdf, say).ConfigureAwait(true);
            return;
        }
        var path = Path.Combine(Folder(), SupplierReturnDocuments.FileName(data, "pdf"));
        await Task.Run(() => SupplierReturnDocumentRenderer.SavePdf(data, path)).ConfigureAwait(true);
        PurchaseDocs.OpenFile(path);
        say(T("Принтер не найден — накладная сохранена в PDF: ", "Принтер табылган жок — накладной PDF форматында сакталды: ", "No printer found — the waybill was saved as PDF: ",
            "Yazıcı bulunamadı — irsaliye PDF olarak kaydedildi: ", "Printer topilmadi — yuk xati PDF qilib saqlandi: ") + path);
    }

    private static async Task SaveAsync(Control host, SupplierReturnDocData data, Format format, Action<string> say)
    {
        if (TopLevel.GetTopLevel(host) is not { } top)
            return;
        var extension = format switch { Format.Word => "docx", Format.Excel => "xlsx", _ => "pdf" };
        var kind = format switch { Format.Word => "Word", Format.Excel => "Excel", _ => "PDF" };
        var options = new FilePickerSaveOptions
        {
            Title = T("Сохранить накладную: ", "Накладнойду сактоо: ", "Save the waybill: ", "İrsaliyeyi kaydet: ", "Yuk xatini saqlash: ") + kind,
            SuggestedFileName = SupplierReturnDocuments.FileName(data, extension),
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(kind) { Patterns = [$"*.{extension}"] }],
        };
        if (OperatingSystem.IsWindows())
        {
            try
            {
                options.SuggestedStartLocation = await top.StorageProvider.TryGetFolderFromPathAsync(Folder()).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Накладная возврата: стартовая папка не открыта ({ex.Message}).", "INFO");
            }
        }
        var file = await top.StorageProvider.SaveFilePickerAsync(options).ConfigureAwait(true);
        if (file is null)
        {
            say("");
            return;
        }
        var path = ExportFileBridge.PathFor(file); // на Android без локального пути — через временный файл
        say(T("Сохраняю…", "Сакталууда…", "Saving…", "Kaydediliyor…", "Saqlanmoqda…"));
        await Task.Run(() =>
        {
            switch (format)
            {
                case Format.Word:
                    SupplierReturnDocumentOffice.SaveWord(data, path);
                    break;
                case Format.Excel:
                    SupplierReturnDocumentOffice.SaveExcel(data, path);
                    break;
                default:
                    SupplierReturnDocumentRenderer.SavePdf(data, path);
                    break;
            }
        }).ConfigureAwait(true);
        var local = file.TryGetLocalPath();
        var shown = await ExportFileBridge.CompleteAsync(file, path).ConfigureAwait(true);
        PosLogger.Log($"Накладная возврата {data.Number}: сохранена {kind} ({shown}).", "INFO");
        if (!string.IsNullOrEmpty(local) && !OperatingSystem.IsAndroid())
            PurchaseDocs.OpenFile(local);
        say(T("Файл сохранён: ", "Файл сакталды: ", "File saved: ", "Dosya kaydedildi: ", "Fayl saqlandi: ") + shown);
    }
}
