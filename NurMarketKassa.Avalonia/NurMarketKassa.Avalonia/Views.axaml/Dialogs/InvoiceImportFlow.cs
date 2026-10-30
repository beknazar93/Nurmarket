using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.InvoiceImport;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-10-27, просьба владельца «добавь загрузку электронных накладных»: порядок действий кнопки «Загрузить накладную» в Склад → Приёмка
/// (касса и программа владельца — одно окно). Права → выбор файла → разбор в фоне → (при нужде) сопоставление столбцов → сверка с каталогом →
/// окно предпросмотра → строки в обычную приёмку (WarehouseViewModel.RestoreReceivingLines) и поставщик. Оприходование — прежней кнопкой «Оприходовать»
/// (фон, без дублей, расход из кассы по настройкам); параллельного пути оприходования здесь нет, PosCheckout*/SyncService*/схема базы не затронуты.</summary>
public static class InvoiceImportFlow
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private static bool _running;

    /// <summary>Есть право на приёмку: тот же доступ, что у раздела «Склад» (кнопка «Оприходовать» отдельного права не имеет).</summary>
    public static bool CanImport()
    {
        try
        {
            return App.GetRequiredService<IPermissionService>().HasPermission(PosPermissions.ViewProducts);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"INVOICE_IMPORT: проверка прав не удалась: {ex.Message}", "WARNING");
            return false;
        }
    }

    public static IReadOnlyList<FilePickerFileType> FileTypes() =>
    [
        new FilePickerFileType(T("Накладные (Excel, CSV, XML)", "Накладнойлор (Excel, CSV, XML)", "Invoices (Excel, CSV, XML)", "Faturalar (Excel, CSV, XML)", "Yuk xatlari (Excel, CSV, XML)"))
        {
            Patterns = ["*.xlsx", "*.csv", "*.xml", "*.txt", "*.tsv"],
        },
        new FilePickerFileType(T("PDF и фото (через ИИ Нур Советника)", "PDF жана сүрөт (Нур Кеңешчинин ЖИси аркылуу)", "PDF and photos (via Nur Advisor AI)", "PDF ve fotoğraf (Nur Danışman yapay zekâsı ile)", "PDF va foto (Nur Maslahatchi AI orqali)"))
        {
            Patterns = ["*.pdf", "*.jpg", "*.jpeg", "*.png"],
        },
    ];

    /// <summary>Нажатие «Загрузить накладную» (path == null — выбрать файл) или файл, брошенный в окно.</summary>
    public static async Task RunAsync(Window owner, WarehouseViewModel vm, string? path = null)
    {
        if (_running)
            return;
        _running = true;
        try
        {
            if (!CanImport())
            {
                PosLogger.Log("INVOICE_IMPORT: нет прав на приёмку.", "WARNING");
                PosDialogs.Error(owner, T("Недостаточно прав для выполнения этой операции.", "Бул операцияны аткарууга укук жетишсиз.", "Insufficient permissions to perform this operation.",
                    "Bu işlemi gerçekleştirmek için yetkiniz yok.", "Bu amalni bajarish uchun huquq yetarli emas."), T("Склад", "Кампа", "Warehouse", "Depo", "Ombor"));
                return;
            }

            if (path is null)
            {
                if (TopLevel.GetTopLevel(owner)?.StorageProvider is not { } storage)
                    return;
                var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = T("Выберите электронную накладную", "Электрондук накладнойду тандаңыз", "Select an electronic invoice", "Elektronik faturayı seçin", "Elektron yuk xatini tanlang"),
                    AllowMultiple = false,
                    FileTypeFilter = FileTypes(),
                }).ConfigureAwait(true);
                path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
                if (string.IsNullOrEmpty(path))
                    return;
            }

            await ImportFileAsync(owner, vm, path).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"INVOICE_IMPORT: загрузка накладной прервана: {ex}", "WARNING");
            PosDialogs.Error(owner, T("Не удалось загрузить накладную: ", "Накладнойду жүктөө мүмкүн болгон жок: ", "Could not load the invoice: ", "Fatura yüklenemedi: ", "Yuk xatini yuklab bo'lmadi: ") + ex.Message);
        }
        finally
        {
            _running = false;
        }
    }

    private static async Task ImportFileAsync(Window owner, WarehouseViewModel vm, string path)
    {
        var name = Path.GetFileName(path);
        var load = await Task.Run(() => InvoiceImportService.Load(path)).ConfigureAwait(true);

        // Заголовки не узнаны — кассир указывает столбцы вручную.
        while (!load.Ok && load.NeedsMapping && load.Table is { } table)
        {
            var map = new InvoiceColumnMapWindow(table, load.HeaderIndex, load.Mapping ?? new InvoiceColumnMapping(), name);
            await map.ShowDialog(owner).ConfigureAwait(true);
            if (map.Mapping is not { } mapping)
                return;
            if (map.Remember)
                InvoiceImportRegistry.SaveMapping(table.Rows[Math.Clamp(map.HeaderIndex, 0, table.Rows.Count - 1)].Cells, mapping);
            load = InvoiceImportService.FromTable(table, map.HeaderIndex, mapping, name, load.FileHash);
            if (!load.Ok && !load.NeedsMapping)
                break;
        }

        if (!load.Ok || load.Document is null)
        {
            if (load.IsImageOrPdf)
                PosDialogs.Info(owner, load.Error ?? "", T("PDF и фото накладной", "Накладнойдун PDF жана сүрөтү", "PDF and photo invoices", "PDF ve fotoğraf faturalar", "PDF va foto yuk xatlari"));
            else
                PosDialogs.Error(owner, load.Error ?? T("Файл не удалось прочитать.", "Файлды окуу мүмкүн болгон жок.", "The file could not be read.", "Dosya okunamadı.", "Faylni o'qib bo'lmadi."),
                    T("Загрузка накладной", "Накладнойду жүктөө", "Loading an invoice", "Fatura yükleme", "Yuk xatini yuklash"));
            return;
        }

        // Поставщики кассы (нужны для подбора по ИНН/названию); без связи список пуст — приёмка допустима и без поставщика.
        try
        {
            await vm.LoadReceivingSuppliersAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"INVOICE_IMPORT: список поставщиков не загрузился: {ex.Message}", "WARNING");
        }
        var suppliers = vm.ReceivingSuppliers.Where(s => !string.IsNullOrEmpty(s.Id)).ToList();

        var doc = load.Document;
        var plan = await Task.Run(() => InvoicePlan.Build(doc)).ConfigureAwait(true);
        var table2 = load.Table;
        var headerIndex = load.HeaderIndex;
        var mapping2 = load.Mapping;
        var hash = load.FileHash;

        var request = new InvoiceImportWindow.Request
        {
            Plan = plan,
            FileName = name,
            FileHash = hash,
            Suppliers = suppliers,
            CurrentSupplier = vm.HasReceivingSupplier ? vm.ReceivingSupplier : null,
            TakenCodes = () => vm.ReceivingLines.Select(l => (string?)l.Barcode).ToList(),
            // 2026-10-30: «＋» — форма «Новый поставщик» с полями из накладной (название, ИНН, номер накладной в примечании), защита от дубля по ИНН/названию,
            // без права «Склад» — подсказка «Попросите владельца» (SupplierResolve).
            CreateSupplier = OfflineModeHelper.UseLocalOperations ? null : async (window, known, prefill) =>
            {
                var created = await SupplierResolve.CreateAsync(window, known, prefill).ConfigureAwait(true);
                if (created is null)
                    return null;
                vm.SelectOrAddReceivingSupplier(created);
                return created;
            },
            RemapColumns = table2 is null ? null : async window =>
            {
                var map = new InvoiceColumnMapWindow(table2, headerIndex, mapping2 ?? new InvoiceColumnMapping(), name);
                await map.ShowDialog(window).ConfigureAwait(true);
                if (map.Mapping is not { } chosen)
                    return null;
                var again = InvoiceImportService.FromTable(table2, map.HeaderIndex, chosen, name, hash);
                if (!again.Ok || again.Document is null)
                {
                    PosDialogs.Error(window, again.Error ?? "");
                    return null;
                }
                if (map.Remember)
                    InvoiceImportRegistry.SaveMapping(table2.Rows[Math.Clamp(map.HeaderIndex, 0, table2.Rows.Count - 1)].Cells, chosen);
                headerIndex = map.HeaderIndex;
                mapping2 = chosen;
                var againDoc = again.Document;
                return await Task.Run(() => InvoicePlan.Build(againDoc)).ConfigureAwait(true);
            },
        };

        var dialog = new InvoiceImportWindow(request);
        await dialog.ShowDialog(owner).ConfigureAwait(true);
        if (dialog.Chosen is not { } result)
        {
            PosLogger.Log($"INVOICE_IMPORT: окно предпросмотра закрыто без переноса ({name}).", "INVOICE_IMPORT");
            return;
        }

        // В приёмке уже есть строки другого поставщика — не смешиваем молча.
        var supplier = result.Supplier;
        var supplierAction = InvoiceSupplierRule.Decide(supplier, vm.HasReceivingSupplier ? vm.ReceivingSupplier : null, vm.ReceivingLines.Count);
        if (supplierAction == ReceivingSupplierAction.ConfirmSwitch)
        {
            var ok = await PosDialogs.ConfirmYesNoModalAsync(owner,
                T($"В приёмке уже есть {vm.ReceivingLines.Count} {Tr.Plural(vm.ReceivingLines.Count, "позиция", "позиции", "позиций")} поставщика «{vm.ReceivingSupplier.Name}». Добавить накладную «{supplier.Name}» к ним? Поставщик приёмки сменится на «{supplier.Name}».",
                    $"Кабыл алууда «{vm.ReceivingSupplier.Name}» жеткирүүчүсүнүн {vm.ReceivingLines.Count} позициясы бар. «{supplier.Name}» накладнойун аларга кошосузбу? Кабыл алуунун жеткирүүчүсү «{supplier.Name}» болуп өзгөрөт.",
                    $"The receiving list already has {vm.ReceivingLines.Count} items from “{vm.ReceivingSupplier.Name}”. Add the “{supplier.Name}” invoice to them? The supplier will change to “{supplier.Name}”.",
                    $"Mal kabulde «{vm.ReceivingSupplier.Name}» tedarikçisinin {vm.ReceivingLines.Count} kalemi var. «{supplier.Name}» faturası onlara eklensin mi? Tedarikçi «{supplier.Name}» olarak değişecek.",
                    $"Qabulda «{vm.ReceivingSupplier.Name}» yetkazib beruvchisining {vm.ReceivingLines.Count} pozitsiyasi bor. «{supplier.Name}» yuk xati ularga qo'shilsinmi? Yetkazib beruvchi «{supplier.Name}» ga o'zgaradi."),
                T("Приёмка", "Кабыл алуу", "Receiving", "Mal kabul", "Qabul"),
                T("Добавить", "Кошуу", "Add", "Ekle", "Qo'shish"), T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish")).ConfigureAwait(true);
            if (!ok)
            {
                PosLogger.Log($"INVOICE_IMPORT: перенос отменён кассиром (в приёмке другой поставщик), {name}.", "INVOICE_IMPORT");
                return;
            }
        }

        // 2026-10-27 (ревью Ск-5): поставщик прихода — только тот, что выбран/сопоставлен в окне накладной. Накладная без поставщика (или поставщик не найден и не создан)
        // раньше оставляла в приёмке ПРЕЖНЕГО (из прошлой накладной или ручного выбора) — приход и долг записывались на чужого. Теперь поставщик приёмки сбрасывается
        // в «Без поставщика»; если в приёмке уже лежат позиции, кассир подтверждает это явно.
        else if (supplierAction is ReceivingSupplierAction.ConfirmReset or ReceivingSupplierAction.Reset)
        {
            if (supplierAction == ReceivingSupplierAction.ConfirmReset)
            {
                var keep = vm.ReceivingSupplier!;
                var okReset = await PosDialogs.ConfirmYesNoModalAsync(owner,
                    T($"В приёмке уже есть {vm.ReceivingLines.Count} {Tr.Plural(vm.ReceivingLines.Count, "позиция", "позиции", "позиций")} поставщика «{keep.Name}», а у этой накладной поставщик не выбран. Добавить накладную к ним? Поставщик приёмки сбросится на «Без поставщика» — приход и долг на «{keep.Name}» не запишутся.",
                        $"Кабыл алууда «{keep.Name}» жеткирүүчүсүнүн {vm.ReceivingLines.Count} позициясы бар, ал эми бул накладнойдо жеткирүүчү тандалган эмес. Аларга кошосузбу? Кабыл алуунун жеткирүүчүсү «Жеткирүүчүсүз» болуп калат — «{keep.Name}» үчүн киреше жана карыз жазылбайт.",
                        $"The receiving list already has {vm.ReceivingLines.Count} items from “{keep.Name}”, and this invoice has no supplier selected. Add it to them? The supplier will be reset to “No supplier” — no receipt or debt will be recorded for “{keep.Name}”.",
                        $"Mal kabulde «{keep.Name}» tedarikçisinin {vm.ReceivingLines.Count} kalemi var, bu faturada ise tedarikçi seçilmedi. Onlara eklensin mi? Tedarikçi «Tedarikçisiz» olarak sıfırlanır — «{keep.Name}» için giriş ve borç yazılmaz.",
                        $"Qabulda «{keep.Name}» yetkazib beruvchisining {vm.ReceivingLines.Count} pozitsiyasi bor, bu yuk xatida esa yetkazib beruvchi tanlanmagan. Ularga qo'shilsinmi? Yetkazib beruvchi «Yetkazib beruvchisiz» ga qaytadi — «{keep.Name}» uchun kirim va qarz yozilmaydi."),
                    T("Приёмка", "Кабыл алуу", "Receiving", "Mal kabul", "Qabul"),
                    T("Добавить", "Кошуу", "Add", "Ekle", "Qo'shish"), T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish")).ConfigureAwait(true);
                if (!okReset)
                {
                    PosLogger.Log($"INVOICE_IMPORT: перенос отменён кассиром (накладная без поставщика, в приёмке «{keep.Name}»), {name}.", "INVOICE_IMPORT");
                    return;
                }
            }
            vm.ReceivingSupplier = vm.ReceivingSuppliers.FirstOrDefault(s => string.IsNullOrEmpty(s.Id)) ?? vm.ReceivingSupplier;
            PosLogger.Log("INVOICE_IMPORT: у накладной нет выбранного поставщика — поставщик приёмки сброшен на «Без поставщика».", "INVOICE_IMPORT");
        }

        if (supplier is not null)
            vm.SelectOrAddReceivingSupplier(supplier);
        var added = vm.RestoreReceivingLines(result.Lines.ToList());

        var supplierKey = InvoiceImportRegistry.SupplierKey(supplier?.Inn is { Length: > 0 } sInn ? sInn : result.FileInn, supplier?.Name ?? result.FileSupplierName, supplier?.Id);
        var entry = new InvoiceImportEntry
        {
            Hash = hash,
            SupplierKey = supplierKey,
            SupplierName = supplier?.Name ?? result.FileSupplierName,
            Keys = InvoiceImportRegistry.Keys(hash, result.Number, result.Date, result.FileInn, result.FileTotal),
            Number = result.Number,
            FileName = name,
            LoadedAt = DateTime.Now,
            InvoiceDate = result.Date,
            Items = added,
            Total = result.Total,
        };
        InvoiceImportRegistry.Record(entry);
        // 2026-10-30: копия исходного файла — ссылка «Накладная» у поступления товара (Склад → товар → Поступления и партии).
        if (path is { Length: > 0 })
            InvoiceImportRegistry.StoreFile(entry, path);
        // 2026-10-27 (ревью Ск-2): перед «Оприходовать» окно снова сверит накладную с журналом (уже оприходована / лежит дважды).
        vm.NoteInvoiceInReceiving(entry);
        // 2026-10-30 (нумерация закупок): номер и дата накладной поставщика уйдут вместе с приходом (в комментарии прихода, рядом с номером документа ПР-…).
        vm.ReceivingSupplierDocNumber = result.Number;
        vm.ReceivingSupplierDocDate = result.Date;
        vm.ConfirmInvoiceRepeats = repeats => ConfirmRepeatsAsync(owner, repeats);
        var newCount = result.Lines.Count(l => l.IsNew);
        PosLogger.Log($"INVOICE_IMPORT: накладная {name} перенесена в приёмку — строк {added}, новых товаров {newCount}, сумма {result.Total:0.##}, поставщик {(supplier is null ? "не выбран" : "выбран")}, пропущено нераспознанных {result.Unrecognized}.", "INVOICE_IMPORT");

        // Номер накладной в приёмку/на сервер как поле не передаётся (в документе прихода NurCRM для него нет поля) — подсказываем кассиру на экране.
        var invoiceLabel = (result.Number.Length > 0 ? "№ " + result.Number : T("накладная", "накладной", "invoice", "fatura", "yuk xati"))
                           + (result.Date is { } d ? " " + T("от", "күнү", "dated", "tarih", "sana") + " " + d.ToString("dd.MM.yyyy") : "");
        vm.ShowReceivingInfo(T($"Накладная {invoiceLabel} добавлена: строк {added}, на {result.Total:N2}. Проверьте цены продажи и нажмите «Оприходовать».",
            $"Накладной {invoiceLabel} кошулду: саптар {added}, {result.Total:N2}. Сатуу баасын текшерип, «Кирешеге алуу» басыңыз.",
            $"Invoice {invoiceLabel} added: {added} lines, {result.Total:N2}. Check the sale prices and press “Receive”.",
            $"Fatura {invoiceLabel} eklendi: {added} satır, {result.Total:N2}. Satış fiyatlarını kontrol edip «Stoğa al»'a basın.",
            $"Yuk xati {invoiceLabel} qo'shildi: {added} qator, {result.Total:N2}. Sotuv narxlarini tekshirib, «Kirim qilish»ni bosing."));
    }

    /// <summary>2026-10-27 (ревью Ск-2): перед «Оприходовать» — накладная уже оприходована раньше или лежит в приёмке дважды. true — провести всё равно.</summary>
    private static Task<bool> ConfirmRepeatsAsync(Window owner, IReadOnlyList<InvoiceImportEntry> repeats)
    {
        var list = string.Join("; ", repeats.Take(3).Select(e => $"№ {(e.Number.Length > 0 ? e.Number : "—")} ({(e.SupplierName.Length > 0 ? e.SupplierName : "—")}, {e.LoadedAt:dd.MM.yyyy HH:mm})"));
        return PosDialogs.ConfirmYesNoModalAsync(owner,
            T($"Накладная уже оприходована раньше или добавлена в приёмку дважды: {list}. Повторное оприходование задвоит остаток и долг поставщику. Оприходовать всё равно?",
                $"Накладной мурун кирешеге алынган же кабыл алууга эки жолу кошулган: {list}. Кайра кирешеге алуу калдыкты жана жеткирүүчүгө карызды эки эсе кылат. Баары бир кирешеге аласызбы?",
                $"This invoice was already received or added to the list twice: {list}. Receiving it again would double the stock and the supplier debt. Receive anyway?",
                $"Bu fatura daha önce stoğa alınmış ya da listeye iki kez eklenmiş: {list}. Tekrar stoğa almak stoğu ve tedarikçi borcunu ikiye katlar. Yine de stoğa alınsın mı?",
                $"Bu yuk xati avval kirim qilingan yoki ro'yxatga ikki marta qo'shilgan: {list}. Qayta kirim qilish qoldiq va yetkazib beruvchiga qarzni ikki baravar qiladi. Baribir kirim qilinsinmi?"),
            T("Приёмка", "Кабыл алуу", "Receiving", "Mal kabul", "Qabul"),
            T("Оприходовать", "Кирешеге алуу", "Receive", "Stoğa al", "Kirim qilish"), T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
    }

    // ------------------------------------------------------------------ перетаскивание файла в окно

    /// <summary>Файл из проводника, брошенный на окно склада (вкладка «Приёмка»), открывает ту же загрузку. <paramref name="active"/> — вкладка приёмки открыта.</summary>
    public static void AttachDrop(Window window, Func<bool> active, Func<WarehouseViewModel> vm)
    {
        DragDrop.SetAllowDrop(window, true);
        window.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = active() && FirstFile(e.Data) is { } p && (InvoiceImportService.IsSupportedExtension(p) || InvoiceImportService.IsImageOrPdfExtension(p)) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        });
        window.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (!active() || FirstFile(e.Data) is not { } path)
                return;
            e.Handled = true;
            Dispatcher.UIThread.Post(async () => await RunAsync(window, vm(), path).ConfigureAwait(true), DispatcherPriority.Background);
        });
    }

    private static string? FirstFile(IDataObject data)
    {
        try
        {
            return data.GetFiles()?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => !string.IsNullOrEmpty(p));
        }
        catch
        {
            return null;
        }
    }
}
