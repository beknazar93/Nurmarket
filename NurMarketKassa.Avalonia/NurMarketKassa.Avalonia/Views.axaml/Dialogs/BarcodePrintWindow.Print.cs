using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

// 2026-11-06: В15г — часть класса BarcodePrintWindow (Print): перенесено из BarcodePrintWindow.cs без изменений логики.
public partial class BarcodePrintWindow
{
    private LabelPrinterSelector.LiveStatus? _live;
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(10) };

    private void UpdatePrinterChip()
    {
        var connected = _printerCombo.SelectedItem is DiscoveredPrinter;
        // 2026-11-01 (П-7): к имени принтера добавлен живой статус («Готов», «Не отвечает», «Нет бумаги»…); печать он не блокирует.
        _printerChipText.Text = connected
            ? ((DiscoveredPrinter)_printerCombo.SelectedItem!).Label + (_live is { } live ? " · " + LabelPrinterSelector.LiveText(live) : "")
            : T("Принтер не подключён", "Принтер туташкан эмес", "Printer not connected", "Yazıcı bağlı değil", "Printer ulanmagan");
        var bad = connected && _live is LabelPrinterSelector.LiveStatus.NoAnswer or LabelPrinterSelector.LiveStatus.Offline or LabelPrinterSelector.LiveStatus.PaperOut or LabelPrinterSelector.LiveStatus.Error;
        Use(_printerDot, Shape.FillProperty, !connected || bad ? "BrushDanger" : _live is LabelPrinterSelector.LiveStatus.Unknown ? "BrushWarning" : "BrushSuccess");
    }

    /// <summary>Раз в 10 с (и при выборе принтера), в фоне и не во время печати.</summary>
    private async Task RefreshLiveStatusAsync()
    {
        if (_printCts != null || _printerCombo.SelectedItem is not DiscoveredPrinter printer)
        {
            return;
        }
        var path = printer.DevicePath;
        var status = await Task.Run(() => LabelPrinterSelector.CheckLive(path)).ConfigureAwait(true);
        if (status == LabelPrinterSelector.LiveStatus.Busy || _printerCombo.SelectedItem is not DiscoveredPrinter now || now.DevicePath != path)
            return;
        _live = status;
        UpdatePrinterChip();
    }

    private async Task LoadPrintersAsync()
    {
        IReadOnlyList<DiscoveredPrinter> list;
        try
        {
            // 2026-11-01: принтер этикеток — без PDF/XPS/OneNote/факса (и без камеры/Bluetooth: см. WinUsbPrinterPort).
            list = await Task.Run(() => PrinterListOverride?.Invoke() ?? LabelPrinterSelector.GetChoices()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Barcode print window: printer enumeration failed: {ex.GetType().Name}", "WARNING");
            list = Array.Empty<DiscoveredPrinter>();
        }
        var savedPath = UserPreferences.Instance.LabelPrinterDevicePath;
        var keep = (_printerCombo.SelectedItem as DiscoveredPrinter)?.DevicePath ?? savedPath;
        var wasInit = _init;
        _init = true;
        _printerCombo.ItemsSource = list.ToList();
        _printerCombo.SelectedItem = list.FirstOrDefault(p => string.Equals(p.DevicePath, keep, StringComparison.OrdinalIgnoreCase));
        _init = wasInit;
        _printersLoaded = true;
        UpdatePrinterChip();
        UpdateSummary();
        _ = RefreshLiveStatusAsync();
    }

    private bool _printersLoaded;

    // ------------------------------------------------------------------ печать

    private void SetStatus(string text) => _status.Text = text;

    private async Task PrintTestAsync()
    {
        var sample = new BarcodePrintItem(new CatalogProductTileVm("test", T("Образец товара", "Товардын үлгүсү", "Sample product", "Örnek ürün", "Mahsulot namunasi"), "85.00 сом", false) { Barcode = "4870145004807" }, "4870145004807", 1);
        await PrintLabelsAsync(new[] { sample }, test: true).ConfigureAwait(true);
    }

    private async Task PrintOneAsync(CatalogProductTileVm p)
    {
        var st = GetSel(p.Id);
        var choices = BarcodePrintCatalog.Choices(p);
        var choice = choices.Count == 0 ? null : choices[Math.Clamp(st.BarcodeIndex, 0, choices.Count - 1)];
        await PrintLabelsAsync(new[] { new BarcodePrintItem(p, choice?.Code, CopiesOf(p, st),
            choice is { IsMain: false } ? choice.VariantName : null, choice is { IsMain: false } ? choice.Quantity : 0, choice?.Price) }, test: false).ConfigureAwait(true);
    }

    /// <summary>«Печать выбранных (N)» — на термопринтер этикеток.</summary>
    public async Task PrintSelectedAsync()
    {
        var items = BuildItems();
        if (items.Count == 0)
        {
            SetStatus(T("Выберите хотя бы один товар.", "Жок дегенде бир товарды тандаңыз.", "Select at least one product.", "En az bir ürün seçin.", "Kamida bitta mahsulotni tanlang."));
            return;
        }
        var checkedItems = await ResolveProblemsAsync(items).ConfigureAwait(true); // 2026-11-01 (П-2)
        if (checkedItems is null)
            return;
        if (checkedItems.Count == 0)
        {
            SetStatus(T("Все выбранные сняты с печати.", "Тандалгандардын баары басуудан алынды.", "All selected products were taken off the print.", "Seçilenlerin hepsi yazdırmadan çıkarıldı.", "Tanlanganlarning hammasi chopdan olib tashlandi."));
            return;
        }
        await PrintLabelsAsync(checkedItems, test: false).ConfigureAwait(true);
    }

    /// <summary>2026-11-06: выбранный «Формат кода» для этикеток размеров.</summary>
    private LabelCodeMode CodeMode => (LabelCodeMode)Math.Clamp(_codeModeCombo.SelectedIndex, 0, 2);

    /// <summary>2026-11-06: код принадлежит размеру одежды (справочник размеров кассы), а не основному/доп. штрихкоду товара.</summary>
    private static bool IsSizeCode(string? code) => !string.IsNullOrWhiteSpace(code) && VariantBarcodeIndex.Contains(code);

    private async Task PrintLabelsAsync(IReadOnlyList<BarcodePrintItem> items, bool test)
    {
        if (!BarcodePrintAccess.IsAllowed()) // 2026-11-01 (П-10): вызов кода напрямую — понятное сообщение
        {
            SetStatus(BarcodePrintAccess.DeniedText);
            return;
        }
        // 2026-11-01 (С-9): System.Drawing — только Windows; на Linux/Android честно говорим, что печать здесь недоступна.
        if (!LabelPrinterSelector.PrintingSupported)
        {
            SetStatus(LabelPrinterSelector.UnsupportedText);
            return;
        }
        if (_printerCombo.SelectedItem is null && !_printersLoaded)
        {
            // 2026-11-01 (Н-2): список принтеров ещё ищется (WinUSB/LPT/COM — до нескольких секунд) — не ругаемся «не выбран».
            SetStatus(T("Ищу принтеры… повторите печать через секунду.", "Принтерлер изделүүдө… бир секунддан кийин кайра басыңыз.", "Looking for printers… try printing again in a second.", "Yazıcılar aranıyor… bir saniye sonra tekrar deneyin.", "Printerlar qidirilmoqda… bir soniyadan keyin qayta urinib ko'ring."));
            return;
        }
        if (_printerCombo.SelectedItem is not DiscoveredPrinter printer)
        {
            // 2026-11-01: принтер не выбран или не найден — понятная подсказка (Настройки → Печать) и открытые настройки этикетки.
            SetStatus(LabelPrinterSelector.MissingHint());
            SetSettingsOpen(true);
            return;
        }
        // 2026-11-01 (С-5): большая пачка — сначала подтверждение с числом этикеток и оценкой ленты.
        var totalLabels = items.Sum(i => i.Copies);
        if (!test && totalLabels > LabelPrinterSelector.BigBatchLabels)
        {
            var bigQuestion = LabelPrinterSelector.BigBatchQuestion(totalLabels, _template.HeightMm, UserPreferences.Instance.LabelGapMm, 0, printer.Label);
            var bigOk = ConfirmOverride != null
                ? await ConfirmOverride(bigQuestion).ConfigureAwait(true)
                : await PosConfirmDialog.ShowModalAsync(this, T("Большая печать", "Чоң басуу", "Large print job", "Büyük baskı", "Katta chop etish"), bigQuestion,
                    T($"Напечатать {totalLabels}", $"{totalLabels} басуу", $"Print {totalLabels}", $"{totalLabels} yazdır", $"{totalLabels} ta chop etish"), T("Отмена", "Жок", "Cancel", "İptal", "Bekor qilish")).ConfigureAwait(true);
            if (!bigOk)
                return;
        }
        // 2026-11-06: QR для размеров должен быть читаемым на этой этикетке — иначе не печатаем (этикетка вышла бы без кода).
        if (!test && CodeMode == LabelCodeMode.Qr)
        {
            foreach (var it in items)
            {
                if (IsSizeCode(it.Barcode) && LabelCodeModes.QrSizeProblem(_template, it.Barcode) is { } qrProblem)
                {
                    SetStatus(qrProblem);
                    return;
                }
            }
        }
        if (!_shell.TryBeginBusy())
            return;
        SaveState();
        var template = _template.Clone();
        var sizeTemplate = LabelCodeModes.Apply(template, CodeMode); // 2026-11-06: для этикеток размеров
        var store = UserPreferences.Instance.StoreName;
        var print = ThermalPrintOverride ?? BarcodeLabelService.Print;
        _printCts = new CancellationTokenSource();
        var ct = _printCts.Token;
        _progress.Maximum = Math.Max(items.Count, 1);
        _progress.Value = 0;
        _progress.IsVisible = items.Count > 1;
        _cancelPrintButton.IsVisible = items.Count > 1;
        _cancelPrintButton.IsEnabled = true;
        foreach (var b in new[] { _selectPageButton, _selectFoundButton, _clearButton, _a4Button, _printButton })
            b.IsEnabled = false;
        SetStatus(T($"Печать… (товаров: {items.Count})", $"Басып жатат… (товар: {items.Count})", $"Printing… ({items.Count} products)", $"Yazdırılıyor… ({items.Count} ürün)", $"Chop etilmoqda… ({items.Count} ta mahsulot)"));
        var done = 0;
        var labels = 0;
        var last = LabelPrintResult.Success;
        var cancelled = false;
        // 2026-11-01 (В-3): какие товары напечатаны целиком и на каком остановились — выбор с напечатанных снимается после сбоя/отмены.
        var doneIds = new List<string>();
        string? stoppedAt = null;
        string? partialId = null; // товар, у которого часть этикеток ушла (PartiallySent) — выбор и «копии = осталось» сохраняются
        var partialRemaining = 0;
        var partialSent = 0;
        try
        {
            await Task.Run(() =>
            {
                foreach (var it in items)
                {
                    if (ct.IsCancellationRequested)
                    {
                        cancelled = true;
                        stoppedAt = it.Product.Title;
                        break;
                    }
                    var request = new LabelPrintRequest(it.Product.Title, it.Barcode ?? "", BarcodePrintCatalog.LabelPrice(it), it.Copies, printer.DevicePath,
                        IsSizeCode(it.Barcode) ? sizeTemplate : template, it.Product.Article, it.Product.Unit, store, it.VariantText, Guid.NewGuid().ToString("N"));
                    last = print(request);
                    if (last == LabelPrintResult.PartiallySent && BarcodeLabelService.PartialOfJob(request.JobId) is { } part)
                    {
                        partialId = it.Product.Id;
                        partialSent = Math.Clamp(part.Sent, 0, it.Copies);
                        partialRemaining = Math.Clamp(it.Copies - partialSent, 1, 99);
                    }
                    if (last != LabelPrintResult.Success)
                    {
                        stoppedAt = it.Product.Title;
                        break;
                    }
                    doneIds.Add(it.Product.Id);
                    done++;
                    labels += it.Copies;
                    var d = done;
                    Dispatcher.UIThread.Post(() => _progress.Value = d);
                }
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Barcode print window: print failed: {ex}", "ERROR");
            last = LabelPrintResult.Failed;
        }
        finally
        {
            _progress.IsVisible = false;
            _cancelPrintButton.IsVisible = false;
            _printCts?.Dispose();
            _printCts = null;
            _shell.EndBusy();
            _clearButton.IsEnabled = _selectPageButton.IsEnabled = _selectFoundButton.IsEnabled = true;
            UpdateSelectionUi();
        }

        if (labels > 0)
            LabelPrinterSelector.LogSent(printer, printer.DevicePath, labels);
        // 2026-11-01 (В-3): после сбоя или отмены посреди пачки с напечатанных товаров выбор снимается — повторное «Печать» не печатает их заново.
        var partialNote = "";
        if (!test && partialId is not null)
        {
            // 2026-11-02 (аудит №2, С3-09): у недопечатанного товара остаются выбор и число копий «осталось» — повторное «Печать» не печатает уже ушедшие
            GetSel(partialId).Copies = partialRemaining;
            SetSelected(GetSel(partialId), true);
            foreach (var s in _copiesSync)
                s();
            partialNote += " " + T($"«{stoppedAt}»: ушло {partialSent}, осталось допечатать {partialRemaining} — выбор и копии сохранены.", $"«{stoppedAt}»: {partialSent} кетти, {partialRemaining} басыш керек — тандоо жана көчүрмөлөр сакталды.",
                $"“{stoppedAt}”: {partialSent} sent, {partialRemaining} left to print — the selection and copies are kept.", $"«{stoppedAt}»: {partialSent} gitti, {partialRemaining} basılacak — seçim ve kopyalar korundu.", $"«{stoppedAt}»: {partialSent} ketdi, {partialRemaining} chop etish kerak — tanlov va nusxalar saqlandi.");
        }
        if (!test && (last != LabelPrintResult.Success || cancelled) && doneIds.Count > 0)
        {
            foreach (var id in doneIds)
                SetSelected(GetSel(id), false);
            foreach (var s in _pageSync)
                s();
            UpdateSelectionUi();
            SchedulePreview();
            partialNote = " " + T("Выбор снят с напечатанных — повторное «Печать» допечатает остальные.", "Басылгандардан тандоо алынды — кайра «Басуу» калгандарын басат.",
                "Printed products were deselected — pressing Print again prints only the rest.", "Yazdırılanların seçimi kaldırıldı — tekrar «Yazdır» kalanları yazdırır.", "Chop etilganlardan tanlov olib tashlandi — qayta «Chop etish» qolganlarini chop etadi.");
        }
        var problems = test ? 0 : items.Count(i => BarcodeLabelService.DescribeBarcodeProblem(i.Barcode, IsSizeCode(i.Barcode) ? sizeTemplate : template) is not null);
        var problemNote = problems == 0 ? "" : " " + T(
            $"Замечания по штрихкоду у {problems} товаров (неверный EAN — напечатан как Code 128, кириллица, слишком длинный или плотный код).",
            $"{problems} товардын штрих-кодунда эскертүү бар (туура эмес EAN — Code 128 катары басылды, кирилл, өтө узун же тыгыз код).",
            $"Barcode notes for {problems} products (invalid EAN printed as Code 128, Cyrillic, too long or too dense).",
            $"{problems} üründe barkod uyarısı var (geçersiz EAN Code 128 olarak basıldı, Kiril, çok uzun veya çok sık).",
            $"{problems} ta mahsulotda shtrix-kod bo'yicha eslatma bor (noto'g'ri EAN Code 128 sifatida chop etildi, kirill, juda uzun yoki zich).");
        SetStatus(last switch
        {
            LabelPrintResult.Success when cancelled => T($"Печать остановлена: отправлено {labels} этикеток ({done} из {items.Count} товаров).", $"Басуу токтотулду: {labels} этикетка жөнөтүлдү ({done}/{items.Count} товар).", $"Printing stopped: {labels} labels sent ({done} of {items.Count} products).", $"Yazdırma durduruldu: {labels} etiket gönderildi ({done}/{items.Count} ürün).", $"Chop etish to'xtatildi: {labels} ta yorliq yuborildi ({done}/{items.Count} mahsulot).") + partialNote,
            LabelPrintResult.Success => T($"Готово — отправлено на печать {labels} этикеток.", $"Даяр — {labels} этикетка басууга жөнөтүлдү.", $"Done — {labels} labels sent to the printer.", $"Tamamlandı — {labels} etiket yazdırmaya gönderildi.", $"Tayyor — {labels} ta yorliq chop etishga yuborildi.") + " " + LabelPrinterSelector.SentText(printer.Label) + "." + problemNote,
            LabelPrintResult.PartiallySent => T($"Напечатано {done} из {items.Count} товаров ({labels} этикеток); на «{stoppedAt}» — не всё. ", $"{done}/{items.Count} товар басылды ({labels} этикетка); «{stoppedAt}» боюнча баары эмес. ", $"Printed {done} of {items.Count} products ({labels} labels); not everything for “{stoppedAt}”. ", $"{done}/{items.Count} ürün basıldı ({labels} etiket); «{stoppedAt}» için hepsi değil. ", $"{done}/{items.Count} mahsulot chop etildi ({labels} ta yorliq); «{stoppedAt}» bo'yicha hammasi emas. ") + LabelPrinterSelector.PartialText() + partialNote,
            LabelPrintResult.PrinterNotFound => T("Принтер не найден — обновите список принтеров.", "Принтер табылган жок — принтерлердин тизмесин жаңыртыңыз.", "Printer not found — refresh the printer list.", "Yazıcı bulunamadı — yazıcı listesini yenileyin.", "Printer topilmadi — printerlar ro'yxatini yangilang."),
            _ => T($"Ошибка печати: напечатано {done} из {items.Count} товаров ({labels} этикеток), остановились на «{stoppedAt}». Подробности в журнале приложения.", $"Басуу катасы: {done}/{items.Count} товар басылды ({labels} этикетка), «{stoppedAt}» боюнча токтоду. Толук маалымат колдонмонун журналында.", $"Printing error: {done} of {items.Count} products printed ({labels} labels), stopped at “{stoppedAt}”. See the app log.", $"Yazdırma hatası: {done}/{items.Count} ürün basıldı ({labels} etiket), «{stoppedAt}» üründe durdu. Ayrıntılar uygulama günlüğünde.", $"Chop etishda xato: {done}/{items.Count} mahsulot chop etildi ({labels} ta yorliq), «{stoppedAt}» da to'xtadi. Batafsil ma'lumot ilova jurnalida.") + partialNote,
        });
    }

    // ------------------------------------------------------------------ A4

    public async Task OpenA4Async()
    {
        var items = BuildItems();
        if (items.Count == 0)
        {
            SetStatus(T("Выберите хотя бы один товар.", "Жок дегенде бир товарды тандаңыз.", "Select at least one product.", "En az bir ürün seçin.", "Kamida bitta mahsulotni tanlang."));
            return;
        }
        var checkedItems = await ResolveProblemsAsync(items).ConfigureAwait(true); // 2026-11-01 (П-2)
        if (checkedItems is null || checkedItems.Count == 0)
            return;
        items = checkedItems;
        // «= остаток»: у каждого свои копии — передаём их явно
        var overrides = items.Select(i => _settings.CopiesMode == "stock" ? i.Copies : (_sel.TryGetValue(i.Product.Id, out var s) ? s.Copies : null)).ToList();
        // 2026-11-01 (С-1): «Копий (по умолчанию)» главного окна — общее число копий листа A4 (раньше брался отдельный A4Copies = 1).
        _settings.A4Copies = Math.Clamp(_settings.DefaultCopies, 1, 99);
        var a4 = new BarcodeA4SheetWindow(items, overrides, _settings, UserPreferences.Instance.StoreName);
        await a4.ShowDialog(this).ConfigureAwait(true);
        if (!string.IsNullOrEmpty(a4.ResultMessage))
            SetStatus(a4.ResultMessage);
    }

    // ------------------------------------------------------------------ штрихкод для товара без кода

    private async Task CreateBarcodeAsync(CatalogProductTileVm p)
    {
        // Только для товара без штрихкода: ничего не стирается, у товара появляется один новый код.
        if (BarcodePrintCatalog.HasBarcode(p))
            return;
        var code = BarcodeGenerator.GenerateForCatalog();
        var question = T($"Сгенерировать штрихкод {code} и сохранить его в карточку товара «{p.Title}»? Изменение появится в NurCRM и на всех кассах.",
            $"{code} штрих-кодун түзүп, «{p.Title}» товарынын карточкасына сактайсызбы? Өзгөртүү NurCRM'де жана бардык кассаларда пайда болот.",
            $"Generate barcode {code} and save it to the card of “{p.Title}”? The change will appear in NurCRM and on all registers.",
            $"{code} barkodu üretilip «{p.Title}» ürün kartına kaydedilsin mi? Değişiklik NurCRM'de ve tüm kasalarda görünür.",
            $"{code} shtrix-kodini yaratib, «{p.Title}» mahsuloti kartochkasiga saqlaymizmi? O'zgarish NurCRM da va barcha kassalarda paydo bo'ladi.");
        var ok = ConfirmOverride != null
            ? await ConfirmOverride(question).ConfigureAwait(true)
            : await PosConfirmDialog.ShowModalAsync(this, T("Создать штрих-код", "Штрих-код түзүү", "Create a barcode", "Barkod oluştur", "Shtrix-kod yaratish"), question,
                T("Создать и сохранить", "Түзүп сактоо", "Create and save", "Oluştur ve kaydet", "Yaratib saqlash"), T("Отмена", "Жок", "Cancel", "İptal", "Bekor qilish")).ConfigureAwait(true);
        if (!ok)
            return;
        if (!_shell.TryBeginBusy())
            return;
        string? error;
        try
        {
            SetStatus(T("Сохраняю штрихкод в карточку товара…", "Штрих-кодду товардын карточкасына сактап жатам…", "Saving the barcode to the product card…", "Barkod ürün kartına kaydediliyor…", "Shtrix-kod mahsulot kartochkasiga saqlanmoqda…"));
            error = BarcodeSaveOverride != null
                ? await BarcodeSaveOverride(p.Id, code).ConfigureAwait(true)
                : await SaveBarcodeToServerAsync(p, code).ConfigureAwait(true);
        }
        finally
        {
            _shell.EndBusy();
        }
        if (error != null)
        {
            SetStatus(error);
            return;
        }
        p.Barcode = code;
        SetStatus(T($"Штрихкод {code} сохранён в карточке «{p.Title}».", $"{code} штрих-коду «{p.Title}» карточкасына сакталды.", $"Barcode {code} saved to the card of “{p.Title}”.", $"{code} barkodu «{p.Title}» kartına kaydedildi.", $"{code} shtrix-kodi «{p.Title}» kartochkasiga saqlandi."));
        RenderPage();
        UpdateSelectionUi();
    }

    private static async Task<string?> SaveBarcodeToServerAsync(CatalogProductTileVm p, string code)
    {
        var result = await ProductActions.UpdateFieldsAsync(p.Id, new Dictionary<string, object?> { ["barcode"] = code },
            T("штрихкод", "штрих-код", "barcode", "barkod", "shtrix-kod"), PosApp.CurrentUserDisplayName ?? PosApp.CurrentUserId).ConfigureAwait(true);
        return result.Ok ? null : result.Message;
    }
}
