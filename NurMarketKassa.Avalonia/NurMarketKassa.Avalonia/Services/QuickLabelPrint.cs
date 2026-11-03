using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-01 (просьба владельца: печать этикетки ОДНОЙ кнопкой без окна): значок «🏷 Печать этикетки» на строке товара отправляет этикетку
/// по шаблону по умолчанию на выбранный принтер этикеток (USB, очередь Windows или сеть) без окна: число копий — «Копий (по умолчанию)» из окна
/// массовой печати, внизу короткое «Отправлено на <принтер>». Если что-то мешает — принтер не выбран или не найден, нет штрихкода, у штрихкода
/// есть замечание (П-2) — открывается прежнее окно «Этикетка» с предпросмотром и объяснением; нет права «Печать штрих-кодов» (П-10) — сообщение.</summary>
public static class QuickLabelPrint
{
    private static int _busy;

    /// <summary>Печать одной кнопкой. Все ошибки показываются кассиру, исключений наружу нет.</summary>
    public static async Task RunAsync(Window? owner, CatalogProductTileVm product)
    {
        if (!BarcodePrintAccess.Ensure(owner))
            return;
        if (!LabelPrinterSelector.PrintingSupported)
        {
            PosMessageBox.Show(owner, LabelPrinterSelector.UnsupportedText, Tr.T("Этикетка", "Этикетка", "Label", "Etiket", "Yorliq"));
            return;
        }
        // Повторное нажатие, пока идёт печать, ничего не делает (двойной клик не даёт две этикетки).
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return;
        bool handled;
        try
        {
            handled = await TryPrintAsync(owner, product).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Quick label print failed: {ex.GetType().Name}", "ERROR");
            handled = false;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
        if (!handled)
            await OpenWindowAsync(owner, product).ConfigureAwait(true);
    }

    /// <summary>true — этикетка отправлена или кассиру показана ошибка; false — нужно прежнее окно (нет штрихкода, замечание, принтер не выбран).</summary>
    private static async Task<bool> TryPrintAsync(Window? owner, CatalogProductTileVm product)
    {
        var template = LabelTemplateStore.Load();
        var barcode = product.Barcode?.Trim();
        // Нет штрихкода — окно объяснит («У товара не указан штрих-код»).
        if (template.Barcode.Enabled && string.IsNullOrWhiteSpace(barcode))
            return false;
        // П-2: замечание по штрихкоду (неверная контрольная цифра, кириллица, слишком длинный или плотный код) — не молчаливая печать, а окно.
        if (template.Barcode.Enabled && BarcodeLabelService.DescribeBarcodeProblem(barcode, template) is not null)
            return false;

        var copies = Math.Clamp(BarcodePrintSettingsStore.Load().DefaultCopies, 1, 99);
        var name = product.Title;
        var priceText = BarcodePrintCatalog.LabelPrice(product);
        var article = product.Article;
        var unit = product.Unit;
        var store = UserPreferences.Instance.StoreName;

        // Принтер ищется и печать идёт в фоне — окно кассы не замирает.
        var outcome = await Task.Run(() =>
        {
            var resolution = LabelPrinterSelector.ResolveFast(); // без полного перебора устройств на каждый клик
            if (!resolution.IsReady || resolution.Printer is null)
                return (Result: (LabelPrintResult?)null, Printer: (DiscoveredPrinter?)null);
            var request = new LabelPrintRequest(name, barcode ?? "", priceText, copies, resolution.Printer.DevicePath, template, article, unit, store);
            var result = BarcodeLabelService.Print(request);
            if (result == LabelPrintResult.Success)
                LabelPrinterSelector.LogSent(resolution.Printer, resolution.Printer.DevicePath, copies);
            return (Result: (LabelPrintResult?)result, Printer: resolution.Printer);
        }).ConfigureAwait(true);

        if (outcome.Result is null || outcome.Printer is null)
            return false; // принтер этикеток не выбран или не найден — окно покажет подсказку и список
        if (outcome.Result == LabelPrintResult.Success)
        {
            SuccessToast.Show(LabelPrinterSelector.SentText(outcome.Printer.Label));
            return true;
        }
        if (outcome.Result == LabelPrintResult.PartiallySent)
        {
            PosMessageBox.Show(owner, LabelPrinterSelector.PartialText(outcome.Printer.DevicePath), Tr.T("Этикетка", "Этикетка", "Label", "Etiket", "Yorliq"));
            return true;
        }
        PosMessageBox.Show(owner, outcome.Result == LabelPrintResult.PrinterNotFound
                ? Tr.T("Принтер не найден — проверьте питание и кабель или выберите другой в «Этикетка…».", "Принтер табылган жок — кубатты жана кабелди текшериңиз же «Этикетка…» ичинен башкасын тандаңыз.", "Printer not found — check power and cable, or choose another in “Label…”.", "Yazıcı bulunamadı — güç ve kabloyu kontrol edin veya «Etiket…» içinden başkasını seçin.", "Printer topilmadi — quvvat va kabelni tekshiring yoki «Yorliq…» dan boshqasini tanlang.")
                : Tr.T("Ошибка печати этикетки. Подробности в журнале приложения; можно открыть «Этикетка…» и выбрать другой принтер.", "Этикетка басуу катасы. Чоо-жайы колдонмонун журналында; «Этикетка…» ачып, башка принтерди тандаса болот.", "Label print error. See the application log; you can open “Label…” and choose another printer.", "Etiket yazdırma hatası. Ayrıntılar uygulama günlüğünde; «Etiket…» açıp başka bir yazıcı seçebilirsiniz.", "Yorliq chop etish xatosi. Tafsilotlar dastur jurnalida; «Yorliq…» ni ochib, boshqa printerni tanlash mumkin."),
            Tr.T("Этикетка", "Этикетка", "Label", "Etiket", "Yorliq"));
        return true;
    }

    /// <summary>Прежнее окно «Этикетка» (предпросмотр, копии, выбор принтера). Право «Печать штрих-кодов» проверяется.</summary>
    public static async Task OpenWindowAsync(Window? owner, CatalogProductTileVm product)
    {
        if (!BarcodePrintAccess.Ensure(owner))
            return;
        var dialog = new LabelPrintDialog(product);
        if (owner is null)
            dialog.Show();
        else
            await dialog.ShowDialog(owner).ConfigureAwait(true);
    }
}
