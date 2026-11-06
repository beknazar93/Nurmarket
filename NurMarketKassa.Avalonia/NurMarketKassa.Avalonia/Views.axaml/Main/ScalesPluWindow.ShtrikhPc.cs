using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Platform.Storage;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Views;

// 2026-11-06: весы «ШТРИХ РС-200С2» (ЗАО «Штрих-М», сенсорные, встроенный Windows-ПК, pcscale.exe). Обмен — ФАЙЛАМИ в общей папке весов
// (руководство администратора, п. 7.4, 8.1): касса пишет Import\PCScale.txt (CP1251, «<D…» по 34 полей) и флаг-файл Import\PCScale.flz; весы сами забирают
// файл раз в «Обновление» минут либо по кнопке «Импорт данных». Код товара в файле = «Код в ШК» (то же число весы печатают в штрихкоде этикетки).
// На живых весах не проверено — см. ShtrikhPcImportWriter.
public partial class ScalesPluWindow
{
    private static string ShtrikhPcSubtitle() => Tr.T(
        "Отмеченные товары касса запишет файлом в общую папку весов (Import\\PCScale.txt) и положит флаг-файл. Весы сами заберут их через «Обновление» минут или сразу — «Система и печать» → «Импорт данных». У каждого товара код на весах — колонка «Код в ШК» (по умолчанию PLU).",
        "Белгиленген товарларды касса таразанын жалпы папкасына файл менен жазып (Import\\PCScale.txt), флаг-файл коёт. Тараза аларды «Обновление» мүнөттөн кийин өзү алат же дароо — «Система и печать» → «Импорт данных». Таразадагы ар бир товардын коду — «ШКдагы код» тилкеси (демейки PLU).",
        "The till writes the ticked goods as a file into the scale's shared folder (Import\\PCScale.txt) and puts a flag file. The scale picks them up itself after “Update” minutes or right away — “System and printing” → “Import data”. Each product's code on the scale is the “Barcode code” column (PLU by default).",
        "Kasa işaretli ürünleri tartının paylaşımlı klasörüne dosya olarak yazar (Import\\PCScale.txt) ve bayrak dosyası koyar. Tartı bunları «Обновление» dakika sonra kendisi alır ya da hemen — «Система и печать» → «Импорт данных». Tartıdaki her ürünün kodu — «Barkod kodu» sütunu (varsayılan PLU).",
        "Kassa belgilangan tovarlarni tarozining umumiy papkasiga fayl qilib yozadi (Import\\PCScale.txt) va bayroq-fayl qo‘yadi. Tarozi ularni «Обновление» daqiqadan keyin o‘zi oladi yoki darhol — «Система и печать» → «Импорт данных». Tarozidagi har bir tovar kodi — «ShKdagi kod» ustuni (standart PLU).");

    /// <summary>Строки для файла: код — «Код в ШК» (проверен CheckSendBatch: не чужой товар на кассе, помещается в число знаков кода этикетки), тип — весовой/штучный из карточки.
    /// <paramref name="problems"/> — товары, которые не записываются (⚠ уже стоит в строке таблицы).</summary>
    private (List<ShtrikhPcItem> Items, Dictionary<string, ScaleSentPlu> Written, List<string> RecordIds, int Problems) BuildShtrikhPcItems(List<string> selectedIds, ShtrikhPcSettings settings)
    {
        var byId = NurMarketKassa.Services.CatalogCacheService.Products.ToDictionary(p => p.Id);
        var numbers = PinNumbersForSend(selectedIds);
        var (codes, problems) = CheckSendBatch(selectedIds, numbers, settings.MaxProductCode);
        var items = new List<ShtrikhPcItem>();
        var written = new Dictionary<string, ScaleSentPlu>(StringComparer.Ordinal);
        var ids = new List<string>();
        foreach (var id in selectedIds)
        {
            if (!byId.TryGetValue(id, out var product) || problems.ContainsKey(id) || !codes.TryGetValue(id, out var code) || !numbers.TryGetValue(id, out var plu))
                continue;
            items.Add(new ShtrikhPcItem(code, product.Title, (decimal)LocalCartService.ParsePrice(product.PriceLine), IsPiece: !product.IsWeighted));
            ids.Add(id);
            written[id] = new ScaleSentPlu { Plu = plu, Code = code.ToString(CultureInfo.InvariantCulture), Name = product.Title };
        }
        return (items, written, ids, problems.Count);
    }

    private async Task SendToShtrikhPcAsync()
    {
        var selectedIds = _allRows.Where(r => r.IsSelected).Select(r => r.Id).ToList();
        if (selectedIds.Count == 0)
        {
            StatusText.Text = Tr.T("Выберите хотя бы один товар.", "Жок дегенде бир товарды тандаңыз.", "Select at least one product.", "En az bir ürün seçin.", "Kamida bitta mahsulotni tanlang.");
            return;
        }

        var settings = ShtrikhPcSettings.Current;
        if (settings.Folder.Length == 0)
        {
            StatusText.Text = ScaleUi.PcFolderCheckText(ShtrikhPcFolderCheck.NoFolder, "", "");
            return;
        }

        SendButton.IsEnabled = false;
        StatusText.Text = Tr.T("Запись файла в папку весов…", "Файлды таразанын папкасына жазуу…", "Writing the file to the scale folder…", "Dosya tartı klasörüne yazılıyor…", "Fayl tarozi papkasiga yozilmoqda…");
        SetProgress(0, 1);
        try
        {
            var (items, written, recordIds, problemCount) = BuildShtrikhPcItems(selectedIds, settings);
            var result = await ShtrikhPcImportWriter.WriteAsync(items, settings, overwritePending: false, settings.MaxProductCode).ConfigureAwait(true);
            if (result.Status == ShtrikhPcWriteStatus.PendingNotAccepted)
            {
                // Прежний файл весы ещё не приняли: без подтверждения не затираем.
                var overwrite = await PosDialogHost.ShowAsync(new PosConfirmDialog(
                    Tr.T("Предыдущая загрузка ещё не принята весами", "Мурунку жүктөөнү тараза азырынча кабыл алган жок", "The previous upload has not been accepted by the scale yet", "Önceki yükleme henüz tartı tarafından kabul edilmedi", "Oldingi yuklashni tarozi hali qabul qilmadi"),
                    Tr.T("В папке весов лежит прежний файл со статусом «не обработан» и флаг-файл. Подождите период «Обновление» на весах или нажмите там «Импорт данных». Заменить файл новым сейчас? Товары из прежнего файла, которых нет в новом, на весы не попадут.",
                         "Таразанын папкасында «иштетилген эмес» абалындагы мурунку файл жана флаг-файл турат. Таразадагы «Обновление» мөөнөтүн күтүңүз же ал жерде «Импорт данных» басыңыз. Файлды азыр жаңысына алмаштырабызбы? Мурунку файлдагы, бирок жаңыда жок товарлар таразага түшпөйт.",
                         "The scale folder holds the previous file marked “not processed” and a flag file. Wait for the scale's “Update” period or press “Import data” there. Replace the file with the new one now? Goods that were in the previous file but not in the new one will not reach the scale.",
                         "Tartı klasöründe «işlenmedi» durumunda önceki dosya ve bayrak dosyası duruyor. Tartıdaki «Обновление» süresini bekleyin veya orada «Импорт данных»a basın. Dosya şimdi yenisiyle değiştirilsin mi? Önceki dosyada olup yenisinde olmayan ürünler tartıya ulaşmaz.",
                         "Tarozi papkasida «qayta ishlanmagan» holatdagi oldingi fayl va bayroq-fayl turibdi. Tarozidagi «Обновление» muddatini kuting yoki u yerda «Импорт данных» ni bosing. Faylni hozir yangisiga almashtiramizmi? Oldingi faylda bor, yangisida yo‘q tovarlar taroziga tushmaydi."),
                    confirmText: Tr.T("Заменить файл", "Файлды алмаштыруу", "Replace the file", "Dosyayı değiştir", "Faylni almashtirish"),
                    accent: PosConfirmAccent.Danger), this).ConfigureAwait(true) == true;
                if (!overwrite)
                {
                    StatusText.Text = Tr.T("Предыдущая загрузка ещё не принята весами — файл не заменён. Подождите период «Обновление» или нажмите на весах «Импорт данных».",
                        "Мурунку жүктөөнү тараза азырынча кабыл алган жок — файл алмаштырылган жок. «Обновление» мөөнөтүн күтүңүз же таразада «Импорт данных» басыңыз.",
                        "The previous upload has not been accepted by the scale yet — the file was not replaced. Wait for the “Update” period or press “Import data” on the scale.",
                        "Önceki yükleme henüz tartı tarafından kabul edilmedi — dosya değiştirilmedi. «Обновление» süresini bekleyin veya tartıda «Импорт данных»a basın.",
                        "Oldingi yuklashni tarozi hali qabul qilmadi — fayl almashtirilmadi. «Обновление» muddatini kuting yoki tarozida «Импорт данных» ni bosing.");
                    foreach (var row in _allRows.Where(r => r.IsSelected))
                        row.SetStatus(NotSentText(), RowState.Warning);
                    return;
                }
                result = await ShtrikhPcImportWriter.WriteAsync(items, settings, overwritePending: true, settings.MaxProductCode).ConfigureAwait(true);
            }

            var rowsById = _allRows.ToDictionary(r => r.Id);
            if (result.Status == ShtrikhPcWriteStatus.Ok)
            {
                foreach (var id in recordIds)
                {
                    if (rowsById.TryGetValue(id, out var row))
                        row.SetStatus(SentText() + " · " + Tr.T("код ", "код ", "code ", "kod ", "kod ") + written[id].Code, RowState.Ok);
                }
                RememberSent(written, Array.Empty<int>());
                PosLogger.Log($"Весы Штрих РС: записано {result.Written} товаров в {result.ImportPath}", "SCALES");
                foreach (var warning in result.Warnings)
                    PosLogger.Log($"Весы Штрих РС: {warning}", "SCALES");
                StatusText.Text = Tr.T($"Записано в папку весов: {result.Written} (файл {result.ImportPath}). Весы заберут его сами через «Обновление» минут или сразу — «Система и печать» → «Импорт данных».",
                        $"Таразанын папкасына жазылды: {result.Written} (файл {result.ImportPath}). Тараза аны «Обновление» мүнөттөн кийин өзү алат же дароо — «Система и печать» → «Импорт данных».",
                        $"Written to the scale folder: {result.Written} (file {result.ImportPath}). The scale picks it up itself after “Update” minutes or right away — “System and printing” → “Import data”.",
                        $"Tartı klasörüne yazıldı: {result.Written} (dosya {result.ImportPath}). Tartı onu «Обновление» dakika sonra kendisi alır ya da hemen — «Система и печать» → «Импорт данных».",
                        $"Tarozi papkasiga yozildi: {result.Written} (fayl {result.ImportPath}). Tarozi uni «Обновление» daqiqadan keyin o‘zi oladi yoki darhol — «Система и печать» → «Импорт данных».")
                    + (settings.ClearBase ? Tr.T(" В конце файла команда очистки базы: весы сначала сотрут свои товары.", " Файлдын аягында базаны тазалоо буйругу: тараза адегенде өзүнүн товарларын өчүрөт.", " The file ends with the clear-database command: the scale erases its goods first.", " Dosyanın sonunda veritabanı temizleme komutu var: tartı önce ürünlerini siler.", " Fayl oxirida bazani tozalash buyrug‘i bor: tarozi avval o‘z tovarlarini o‘chiradi.") : "")
                    + (_codeReplaceNote.Length > 0 ? _codeReplaceNote : "")
                    + (result.Warnings.Count > 0 ? " ⚠ " + string.Join(" · ", result.Warnings.Take(3)) + (result.Warnings.Count > 3 ? $" (+{result.Warnings.Count - 3})" : "") : "")
                    + (problemCount > 0 ? Tr.T($" Не записано (⚠ в строках): {problemCount}.", $" Жазылган жок (саптарда ⚠): {problemCount}.", $" Not written (⚠ in rows): {problemCount}.", $" Yazılmadı (satırlarda ⚠): {problemCount}.", $" Yozilmadi (qatorlarda ⚠): {problemCount}.") : "");
                return;
            }

            foreach (var row in _allRows.Where(r => r.IsSelected))
                row.SetStatus(NotSentText(), RowState.Error);
            StatusText.Text = result.Status switch
            {
                ShtrikhPcWriteStatus.NoFolder => ScaleUi.PcFolderCheckText(ShtrikhPcFolderCheck.NoFolder, "", ""),
                ShtrikhPcWriteStatus.FolderUnavailable => ScaleUi.PcFolderCheckText(ShtrikhPcFolderCheck.NotFound, result.Detail ?? "", settings.Folder),
                ShtrikhPcWriteStatus.NoAccess => ScaleUi.PcFolderCheckText(ShtrikhPcFolderCheck.NoAccess, result.Detail ?? "", settings.Folder),
                ShtrikhPcWriteStatus.NothingToWrite => Tr.T("Нечего записывать — исправьте строки с ⚠ (код товара) или выберите товары.", "Жазууга эч нерсе жок — ⚠ белгиси бар саптарды (товар коду) оңдоңуз же товарларды тандаңыз.", "Nothing to write — fix the rows marked ⚠ (item code) or choose goods.", "Yazılacak bir şey yok — ⚠ işaretli satırları (ürün kodu) düzeltin veya ürün seçin.", "Yozish uchun hech narsa yo‘q — ⚠ belgili qatorlarni (tovar kodi) tuzating yoki tovarlarni tanlang.")
                    + (result.Warnings.Count > 0 ? " " + string.Join(" · ", result.Warnings.Take(3)) : ""),
                _ => Tr.T("Не удалось записать файл в папку весов: ", "Файлды таразанын папкасына жазуу мүмкүн болгон жок: ", "Could not write the file to the scale folder: ", "Dosya tartı klasörüne yazılamadı: ", "Faylni tarozi papkasiga yozib bo‘lmadi: ") + result.Detail,
            };
            PosLogger.Log($"Весы Штрих РС: запись в {result.ImportPath} не удалась: {result.Status} {result.Detail}", "SCALES");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Весы Штрих РС: отправка не удалась: {ex}", "SCALES");
            foreach (var row in _allRows.Where(r => r.IsSelected))
                row.SetStatus(NotSentText(), RowState.Error);
            StatusText.Text = Tr.T("Ошибка отправки: ", "Жиберүү катасы: ", "Send error: ", "Gönderme hatası: ", "Yuborish xatosi: ") + ex.Message;
        }
        finally
        {
            HideProgress();
            SendButton.IsEnabled = true;
        }
    }

    /// <summary>«Сохранить файл ▾» у РС-200: файл импорта в выбранную папку/на флешку (ручной «Импорт данных» на весах) или CSV.</summary>
    private void ShowShtrikhPcSaveMenu()
    {
        var usb = new MenuItem { Header = Tr.T("Файл импорта для весов РС-200 (папка / флешка)", "РС-200 таразасы үчүн импорт файлы (папка / флешка)", "Import file for the RS-200 scale (folder / USB stick)", "RS-200 tartı için içe aktarma dosyası (klasör / USB bellek)", "RS-200 tarozisi uchun import fayli (papka / fleshka)") };
        usb.Click += async (_, _) => await SaveShtrikhPcFileAsync().ConfigureAwait(true);
        var csv = new MenuItem { Header = Tr.T("CSV (PLU; название; единица; цена)", "CSV (PLU; аталышы; бирдиги; баасы)", "CSV (PLU; name; unit; price)", "CSV (PLU; ad; birim; fiyat)", "CSV (PLU; nomi; birligi; narxi)") };
        csv.Click += ExportCsv_Click;
        var flyout = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedRight };
        flyout.Items.Add(usb);
        flyout.Items.Add(csv);
        flyout.ShowAt(SaveFileButton);
    }

    /// <summary>Сохраняет файл импорта (без флаг-файла) — для ручного импорта на весах: файл кладётся туда, где весы ждут «Файл импорта»
    /// (по умолчанию Shared\Import\PCScale.txt), затем на весах «Система и печать» → «Импорт данных».</summary>
    private async Task SaveShtrikhPcFileAsync()
    {
        var selectedIds = _allRows.Where(r => r.IsSelected).Select(r => r.Id).ToList();
        if (selectedIds.Count == 0)
            selectedIds = _allRows.Select(r => r.Id).ToList();
        if (selectedIds.Count == 0)
        {
            StatusText.Text = Tr.T("Нечего выгружать: весовых товаров нет.", "Чыгарууга эч нерсе жок: салмактуу товарлар жок.", "Nothing to export: there are no weighed products.", "Dışa aktarılacak bir şey yok: tartılı ürün yok.", "Eksport qilish uchun hech narsa yo'q: vaznli mahsulotlar yo'q.");
            return;
        }

        var settings = ShtrikhPcSettings.Current;
        var (items, written, _, problemCount) = BuildShtrikhPcItems(selectedIds, settings);
        var build = ShtrikhPcImportWriter.Build(items, settings, settings.MaxProductCode);
        if (build.Written == 0)
        {
            StatusText.Text = Tr.T("Нечего записывать — исправьте строки с ⚠ (код товара).", "Жазууга эч нерсе жок — ⚠ белгиси бар саптарды (товар коду) оңдоңуз.", "Nothing to write — fix the rows marked ⚠ (item code).", "Yazılacak bir şey yok — ⚠ işaretli satırları (ürün kodu) düzeltin.", "Yozish uchun hech narsa yo‘q — ⚠ belgili qatorlarni (tovar kodi) tuzating.");
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Tr.T("Файл импорта для весов РС-200", "РС-200 таразасы үчүн импорт файлы", "Import file for the RS-200 scale", "RS-200 tartı için içe aktarma dosyası", "RS-200 tarozisi uchun import fayli"),
            SuggestedFileName = System.IO.Path.GetFileName(settings.ImportFile.Replace('\\', '/')) is { Length: > 0 } name ? name : "PCScale.txt",
            FileTypeChoices = [new FilePickerFileType("TXT") { Patterns = ["*.txt"] }],
        });
        if (file is null)
            return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(ShtrikhPcImportWriter.Encode(build.Text));
        }
        catch (Exception ex)
        {
            StatusText.Text = Tr.T($"Не удалось сохранить файл: {ex.Message}", $"Файлды сактоо мүмкүн болгон жок: {ex.Message}", $"Could not save the file: {ex.Message}", $"Dosya kaydedilemedi: {ex.Message}", $"Faylni saqlab bo'lmadi: {ex.Message}");
            return;
        }
        RememberSent(written, Array.Empty<int>());
        PosLogger.Log($"Весы Штрих РС: файл импорта на {build.Written} товаров сохранён вручную", "SCALES");
        StatusText.Text = Tr.T($"Сохранено товаров: {build.Written}. Положите файл на весы как «{settings.ImportFile}» (папка Shared) и нажмите на весах «Система и печать» → «Импорт данных».",
                $"Сакталган товарлар: {build.Written}. Файлды таразага «{settings.ImportFile}» (Shared папкасы) катары коюп, таразада «Система и печать» → «Импорт данных» басыңыз.",
                $"Goods saved: {build.Written}. Put the file on the scale as “{settings.ImportFile}” (the Shared folder) and press “System and printing” → “Import data” on the scale.",
                $"Kaydedilen ürün: {build.Written}. Dosyayı tartıya «{settings.ImportFile}» (Shared klasörü) olarak koyun ve tartıda «Система и печать» → «Импорт данных»a basın.",
                $"Saqlangan tovarlar: {build.Written}. Faylni taroziga «{settings.ImportFile}» (Shared papkasi) sifatida qo‘ying va tarozida «Система и печать» → «Импорт данных» ni bosing.")
            + (build.Warnings.Count > 0 ? " ⚠ " + string.Join(" · ", build.Warnings.Take(3)) : "")
            + (problemCount > 0 ? Tr.T($" Не записано (⚠ в строках): {problemCount}.", $" Жазылган жок (саптарда ⚠): {problemCount}.", $" Not written (⚠ in rows): {problemCount}.", $" Yazılmadı (satırlarda ⚠): {problemCount}.", $" Yozilmadi (qatorlarda ⚠): {problemCount}.") : "");
    }

    /// <summary>Пример этикетки РС-200 для первого отмеченного товара: раскладка из Настройки → Весы («Штрих РС-200»), вес 0,144 кг, и как её прочтёт касса.</summary>
    private void UpdateShtrikhPcBarcodeExample(ScalePluRowVm? row)
    {
        var s = ShtrikhPcSettings.Current;
        var code = row is not null && long.TryParse(row.BarcodeCode, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) && c > 0 ? c : 1;
        var example = ScaleBarcodeRules.ShtrikhPcExample(s, code, (decimal)(row?.Price ?? 100));
        var head = Tr.T($"Раскладка весов РС-200: префикс {s.BarcodePrefix}, код {s.CodeLength} зн., «{ShtrikhPcSettings.FormatTitle(s.BarcodeFormat)}»{(s.CodeChecksum ? " + КС" : "")} — те же значения должны стоять на весах (вкладка «Штрих-код»). ",
            $"РС-200 таразасынын жайгашуусу: префикс {s.BarcodePrefix}, код {s.CodeLength} белги, «{ShtrikhPcSettings.FormatTitle(s.BarcodeFormat)}»{(s.CodeChecksum ? " + КС" : "")} — ушул эле маанилер таразада да турушу керек («Штрих-код» өтмөгү). ",
            $"RS-200 layout: prefix {s.BarcodePrefix}, code {s.CodeLength} digits, “{ShtrikhPcSettings.FormatTitle(s.BarcodeFormat)}”{(s.CodeChecksum ? " + КС" : "")} — the same values must be set on the scale (“Barcode” tab). ",
            $"RS-200 düzeni: önek {s.BarcodePrefix}, kod {s.CodeLength} hane, «{ShtrikhPcSettings.FormatTitle(s.BarcodeFormat)}»{(s.CodeChecksum ? " + KS" : "")} — aynı değerler tartıda da olmalı («Штрих-код» sekmesi). ",
            $"RS-200 tartibi: prefiks {s.BarcodePrefix}, kod {s.CodeLength} raqam, «{ShtrikhPcSettings.FormatTitle(s.BarcodeFormat)}»{(s.CodeChecksum ? " + KS" : "")} — xuddi shu qiymatlar tarozida ham bo‘lishi kerak («Штрих-код» varag‘i). ");
        var tail = example is null
            ? "⚠ " + Tr.T($"код {code} не помещается в {s.CodeLength} зн. — увеличьте число знаков кода (на весах и в кассе) или поменяйте «Код в ШК».",
                $"{code} коду {s.CodeLength} белгиге батпайт — коддун белгилерин көбөйтүңүз (таразада жана кассада) же «ШКдагы кодду» өзгөртүңүз.",
                $"code {code} does not fit {s.CodeLength} digits — raise the number of code digits (on the scale and in the till) or change the “Barcode code”.",
                $"{code} kodu {s.CodeLength} haneye sığmıyor — kod hane sayısını artırın (tartıda ve kasada) veya «Barkod kodu»nu değiştirin.",
                $"{code} kodi {s.CodeLength} raqamga sig‘maydi — kod raqamlarini oshiring (tarozida va kassada) yoki «ShKdagi kod»ni o‘zgartiring.")
            : Tr.T($"Для «{row?.Name}» (0,144 кг): {example.Value.Barcode} → касса: {example.Value.Reading}.",
                $"«{row?.Name}» үчүн (0,144 кг): {example.Value.Barcode} → касса: {example.Value.Reading}.",
                $"For “{row?.Name}” (0.144 kg): {example.Value.Barcode} → till: {example.Value.Reading}.",
                $"«{row?.Name}» için (0,144 kg): {example.Value.Barcode} → kasa: {example.Value.Reading}.",
                $"«{row?.Name}» uchun (0,144 kg): {example.Value.Barcode} → kassa: {example.Value.Reading}.");
        var off = s.BarcodeEnabled
            ? ""
            : " ⚠ " + Tr.T("Раскладка РС-200 в кассе выключена — Настройки → Весы → «Штрих-код этикеток».", "РС-200 жайгашуусу кассада өчүк — Жөндөөлөр → Таразалар → «Этикетка штрих-коду».", "The RS-200 layout is off in the till — Settings → Scales → “Label barcode”.", "RS-200 düzeni kasada kapalı — Ayarlar → Tartılar → «Etiket barkodu».", "RS-200 tartibi kassada o‘chiq — Sozlamalar → Tarozilar → «Yorliq shtrix-kodi».");
        var amount = s.ByAmount
            ? " ⚠ " + Tr.T("Формат со стоимостью: цены должны быть загружены в весы (иначе в этикетке 0,00).", "Баасы бар формат: баалар таразага жүктөлгөн болушу керек (болбосо этикеткада 0,00).", "Cost format: prices must be loaded into the scale (otherwise the label shows 0.00).", "Tutarlı biçim: fiyatlar tartıya yüklenmiş olmalı (aksi halde etikette 0,00).", "Narxli format: narxlar taroziga yuklangan bo‘lishi kerak (aks holda yorliqda 0,00).")
            : "";
        BarcodeExampleText.Text = head + tail + off + amount;
    }

    /// <summary>«Штрих-код этикетки…» у РС-200: карточка раскладки (префикс, число знаков, формат, КС) в окне.</summary>
    private async Task ShowShtrikhPcBarcodeSettingsAsync()
    {
        var card = new ShtrikhPcBarcodeCard();
        var window = new Window
        {
            Title = Tr.T("Штрих РС-200: раскладка этикетки", "Штрих РС-200: этикетка жайгашуусу", "Shtrih RS-200: label layout", "Shtrih RS-200: etiket düzeni", "Shtrix RS-200: yorliq tartibi"),
        };
        SectionCards.AsModal(window, 760);
        var done = SectionCards.Primary(Tr.T("Готово", "Даяр", "Done", "Tamam", "Tayyor"), isDefault: true);
        done.IsCancel = true;
        done.Click += (_, _) => window.Close();
        card.IsVisible = true;
        window.Content = SectionCards.Shell(window.Title ?? "", null, card, SectionCards.Buttons(done));
        await window.ShowDialog(this).ConfigureAwait(true);
    }
}
