using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-12 (владелец: «а где генерация штрихкода, если их нет?»): массовая генерация штрихкодов в «Штрихкоды и ценники». На панели выбора — «Сгенерировать штрихкоды (N)»,
/// N — товаров без штрихкода в текущем списке (поиск/фильтр; считается по каталогу в памяти, без запросов). Нажатие — подтверждение с примером кода и первыми 10 товарами; затем проход
/// пакетами по 20 (<see cref="BulkBarcodeGenerator"/>) с ходом «Создано N из M» и отменой. Итог — «Создано N, пропущено M, ошибок K», «Распечатать созданные» и «Скопировать журнал».
/// Нет права «Склад: редактирование» или нет связи — кнопка неактивна с подсказкой.</summary>
public partial class BarcodePrintView
{
    private Button _genButton = null!, _genPrintButton = null!, _genLogButton = null!;
    private CancellationTokenSource? _genCts;
    private BulkBarcodeGenerator.RunResult? _genResult;
    private int _genMissing = -1;

    /// <summary>Стенд: есть ли право и связь (в программе — настоящие проверки).</summary>
    public static Func<bool>? GenerateAllowedOverride;

    private IReadOnlyList<Control> BuildGenerateButtons()
    {
        _genButton = Ghost("");
        _genButton.MinWidth = 150;
        _genButton.Click += async (_, _) => await GenerateAsync().ConfigureAwait(true);
        _genPrintButton = Ghost("");
        _genPrintButton.IsVisible = false;
        _genPrintButton.Click += (_, _) => PrintCreated();
        _genLogButton = Ghost(T("Скопировать журнал", "Журналды көчүрүү", "Copy the log", "Günlüğü kopyala", "Jurnalni nusxalash"));
        _genLogButton.IsVisible = false;
        _genLogButton.Click += async (_, _) => await CopyLogAsync().ConfigureAwait(true);
        return new Control[] { _genButton, _genPrintButton, _genLogButton };
    }

    private bool GenerateAllowed(out string reason)
    {
        reason = "";
        if (GenerateAllowedOverride is { } over)
        {
            var ok = over();
            if (!ok)
                reason = T("Нет права «Склад: редактирование» или нет связи с сервером.", "«Кампа: түзөтүү» укугу жок же сервер менен байланыш жок.", "No “Warehouse: edit” permission or no connection to the server.", "«Depo: düzenleme» izni yok veya sunucuyla bağlantı yok.", "«Ombor: tahrirlash» huquqi yo'q yoki server bilan aloqa yo'q.");
            return ok;
        }
        var permissions = NurMarketKassa.AvaloniaHost.App.AppHost?.Services.GetService<IPermissionService>();
        if (permissions is null || !permissions.HasPermission(PosPermissions.EditProducts)) // 2026-11-12 (Л-26): службы прав нет — запрет, а не разрешение
        {
            reason = T("Нет права «Склад: редактирование» — штрихкоды в карточки записывает владелец, администратор или сотрудник с этим правом.", "«Кампа: түзөтүү» укугу жок — штрих-коддорду карточкаларга ээси, администратор же ушул укугу бар кызматкер жазат.", "No “Warehouse: edit” permission — the owner, an administrator or an employee with it saves barcodes to cards.", "«Depo: düzenleme» izni yok — barkodları kartlara sahip, yönetici veya bu izne sahip personel yazar.", "«Ombor: tahrirlash» huquqi yo'q — shtrix-kodlarni kartochkalarga ega, administrator yoki shu huquqli xodim yozadi.");
            return false;
        }
        if (ServerOutageMonitor.IsOutage)
        {
            reason = T("Нет связи с сервером NurCRM — коды записываются в карточки на сервере.", "NurCRM сервери менен байланыш жок — коддор серверде карточкаларга жазылат.", "No connection to the NurCRM server — codes are saved to the cards on the server.", "NurCRM sunucusuyla bağlantı yok — kodlar sunucudaki kartlara yazılır.", "NurCRM serveri bilan aloqa yo'q — kodlar serverdagi kartochkalarga yoziladi.");
            return false;
        }
        return true;
    }

    /// <summary>Кнопки генерации: число товаров без кода в текущем списке, подсвечена при фильтре «Без штрихкода».</summary>
    private void UpdateGenerateUi()
    {
        if (_genButton is null)
            return;
        _genMissing = BulkBarcodeGenerator.CountMissing(_found); // один проход по списку в памяти, без запросов (15 000 товаров — доли миллисекунды)
        var n = _genMissing;
        var running = _genCts != null;
        _genButton.IsVisible = n > 0 || running;
        // 2026-11-12 (UX: панель в две строки на 1366): на узком окне подпись короче
        _genButton.Content = ButtonWithIcon("barcode", _compact
            ? T($"Создать коды ({n:N0})", $"Коддорду түзүү ({n:N0})", $"Make codes ({n:N0})", $"Kod üret ({n:N0})", $"Kod yaratish ({n:N0})")
            : T($"Сгенерировать штрихкоды ({n:N0})", $"Штрих-коддорду түзүү ({n:N0})", $"Generate barcodes ({n:N0})", $"Barkod üret ({n:N0})", $"Shtrix-kodlarni yaratish ({n:N0})"));
        var highlight = _filter == BarcodeFilter.WithoutBarcode;
        if (highlight != _genButton.Classes.Contains("sec-primary"))
        {
            _genButton.Classes.Set("sec-primary", highlight);
            _genButton.Classes.Set("sec-outline", !highlight);
        }
        var allowed = GenerateAllowed(out var reason);
        _genButton.IsEnabled = allowed && !running;
        ToolTip.SetTip(_genButton, !allowed ? reason
            : T($"У {n:N0} товаров нет штрихкода — сгенерировать и сохранить в карточки", $"{n:N0} товарда штрих-код жок — түзүп, карточкаларга сактоо", $"{n:N0} products have no barcode — generate and save to the cards", $"{n:N0} ürünün barkodu yok — üret ve kartlara kaydet", $"{n:N0} ta mahsulotda shtrix-kod yo'q — yaratib, kartochkalarga saqlash"));
        var created = _genResult?.Created.Count ?? 0;
        _genPrintButton.IsVisible = created > 0 && !running;
        _genPrintButton.Content = ButtonWithIcon("printer-outline", T($"Распечатать созданные ({created})", $"Түзүлгөндөрдү басуу ({created})", $"Print the created ({created})", $"Oluşturulanları yazdır ({created})", $"Yaratilganlarni chop etish ({created})"));
        _genLogButton.IsVisible = _genResult is { } r && (r.Created.Count + r.Skipped.Count + r.Failed.Count) > 0 && !running;
    }

    /// <summary>Сколько товаров без штрихкода в текущем списке (стенд).</summary>
    public int MissingBarcodeCount => _genMissing < 0 ? BulkBarcodeGenerator.CountMissing(_found) : _genMissing;

    /// <summary>Итог последнего прохода (стенд).</summary>
    public BulkBarcodeGenerator.RunResult? LastGenerateResult => _genResult;

    /// <summary>Нажатие «Сгенерировать штрихкоды (N)»: подтверждение → проход → итог.</summary>
    public async Task GenerateAsync()
    {
        if (_genCts != null)
            return;
        if (!GenerateAllowed(out var denied))
        {
            SetStatus(denied);
            return;
        }
        var (eligible, skipped) = BulkBarcodeGenerator.Classify(_found);
        if (eligible.Count == 0)
        {
            SetStatus(skipped.Count > 0
                ? T($"Нечего генерировать: у {skipped.Count} товаров без штрихкода код автоматически не создаётся (весовые, комплекты, с размерами).", $"Түзө турган эч нерсе жок: штрих-коду жок {skipped.Count} товарда код автоматтык түзүлбөйт (салмактуу, комплект, өлчөмдүү).", $"Nothing to generate: for {skipped.Count} products without a barcode the code is not created automatically (weighed, bundles, with sizes).", $"Üretilecek bir şey yok: barkodsuz {skipped.Count} üründe kod otomatik üretilmez (tartılan, set, bedenli).", $"Yaratadigan narsa yo'q: shtrix-kodsiz {skipped.Count} ta mahsulotda kod avtomatik yaratilmaydi (tarozida tortiladigan, komplekt, o'lchamli).")
                : T("У всех товаров списка уже есть штрихкод.", "Тизмедеги бардык товарларда штрих-код бар.", "All products in the list already have a barcode.", "Listedeki tüm ürünlerin barkodu var.", "Ro'yxatdagi barcha mahsulotlarda shtrix-kod bor."));
            return;
        }
        var sample = BulkBarcodeGenerator.SampleCode();
        var first = string.Join("\n", eligible.Take(10).Select(p => "• " + p.Title));
        var more = eligible.Count > 10 ? "\n" + T($"… и ещё {eligible.Count - 10:N0}", $"… жана дагы {eligible.Count - 10:N0}", $"… and {eligible.Count - 10:N0} more", $"… ve {eligible.Count - 10:N0} tane daha", $"… va yana {eligible.Count - 10:N0} ta") : "";
        var skipNote = skipped.Count > 0
            ? "\n\n" + T($"Пропустим {skipped.Count:N0}: весовые, комплекты, услуги, упаковки и товары с размерами — их коды заводятся в карточке.", $"{skipped.Count:N0} өткөрүп жиберебиз: салмактуу, комплект, кызмат, таңгак жана өлчөмдүү товарлар — алардын коддору карточкада түзүлөт.", $"We will skip {skipped.Count:N0}: weighed products, bundles, services, packs and products with sizes — their codes are set in the card.", $"{skipped.Count:N0} tanesi atlanacak: tartılan, set, hizmet, paket ve bedenli ürünler — kodları kartta girilir.", $"{skipped.Count:N0} tasi o'tkaziladi: tarozida tortiladigan, komplekt, xizmat, qadoq va o'lchamli mahsulotlar — kodlari kartochkada kiritiladi.")
            : "";
        var question = T($"Сгенерировать EAN-13 для {eligible.Count:N0} товаров и сохранить в карточки?", $"{eligible.Count:N0} товар үчүн EAN-13 түзүп, карточкаларга сактайсызбы?", $"Generate EAN-13 for {eligible.Count:N0} products and save to the cards?", $"{eligible.Count:N0} ürün için EAN-13 üretilip kartlara kaydedilsin mi?", $"{eligible.Count:N0} ta mahsulot uchun EAN-13 yaratib, kartochkalarga saqlaymizmi?")
            + "\n\n" + T($"Пример кода: {sample}", $"Коддун үлгүсү: {sample}", $"Code example: {sample}", $"Kod örneği: {sample}", $"Kod namunasi: {sample}")
            + "\n\n" + T("Первые товары:", "Биринчи товарлар:", "First products:", "İlk ürünler:", "Birinchi mahsulotlar:") + "\n" + first + more + skipNote
            + "\n\n" + T("Изменение появится в NurCRM и на всех кассах; уже существующий штрихкод не затирается.", "Өзгөртүү NurCRM'де жана бардык кассаларда пайда болот; бар штрих-код өзгөртүлбөйт.", "The change will appear in NurCRM and on all registers; an existing barcode is never overwritten.", "Değişiklik NurCRM'de ve tüm kasalarda görünür; mevcut barkod silinmez.", "O'zgarish NurCRM da va barcha kassalarda paydo bo'ladi; mavjud shtrix-kod o'zgartirilmaydi.");
        question += "\n\n" + EstimateText(eligible.Count);
        var ok = await AskAsync(T("Сгенерировать штрихкоды", "Штрих-коддорду түзүү", "Generate barcodes", "Barkod üret", "Shtrix-kodlarni yaratish"), question,
            T("Сгенерировать", "Түзүү", "Generate", "Üret", "Yaratish"), T("Отмена", "Жок", "Cancel", "İptal", "Bekor qilish")).ConfigureAwait(true);
        if (!ok || _genCts != null)
            return;
        _genCts = new CancellationTokenSource();
        var ct = _genCts.Token;
        _genResult = null;
        _progress.Maximum = Math.Max(eligible.Count, 1);
        _progress.Value = 0;
        _progress.IsVisible = true;
        _cancelPrintButton.IsVisible = true;
        _cancelPrintButton.IsEnabled = true;
        _cancelPrintButton.Content = T("Остановить", "Токтотуу", "Stop", "Durdur", "To'xtatish");
        // 2026-11-12 (Л-16): выбор и печать во время генерации не блокируются — проход идёт в фоне, кнопка генерации неактивна, есть «Остановить»
        UpdateGenerateUi();
        var total = eligible.Count;
        SetStatus(T($"Создано 0 из {total:N0}…", $"Түзүлдү 0 / {total:N0}…", $"Created 0 of {total:N0}…", $"Oluşturuldu 0 / {total:N0}…", $"Yaratildi 0 / {total:N0}…"));
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            _progress.Value = p.Done;
            SetStatus(T($"Создано {p.Done:N0} из {p.Total:N0}…", $"Түзүлдү {p.Done:N0} / {p.Total:N0}…", $"Created {p.Done:N0} of {p.Total:N0}…", $"Oluşturuldu {p.Done:N0} / {p.Total:N0}…", $"Yaratildi {p.Done:N0} / {p.Total:N0}…"));
        });
        var result = new BulkBarcodeGenerator.RunResult();
        try
        {
            // карточки каталога правятся через поток интерфейса (Л-17), итог пишется в result по ходу — при сбое остаётся частичный итог (Л-26)
            await Task.Run(() => BulkBarcodeGenerator.RunAsync(eligible, progress, PosApp.CurrentUserDisplayName ?? PosApp.CurrentUserId, ct, a => Dispatcher.UIThread.Post(a), result)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Массовая генерация штрихкодов упала: {ex}", "ERROR");
            var done = result.Created.Count + result.Skipped.Count + result.Failed.Count;
            result.Failed.Add(new BulkBarcodeGenerator.Failure(eligible[Math.Min(done, eligible.Count - 1)], ex.Message));
        }
        finally
        {
            _progress.IsVisible = false;
            _cancelPrintButton.IsVisible = false;
            _cancelPrintButton.Content = T("Отмена печати", "Басууну токтотуу", "Cancel printing", "Yazdırmayı iptal et", "Chop etishni bekor qilish");
            _genCts?.Dispose();
            _genCts = null;
        }
        // 2026-11-13: RunAsync выставляет p.Barcode через Dispatcher.UIThread.Post (асинхронно) — без ожидания RenderPage/UpdateGenerateUi считали «без штрихкода» по ещё не обновлённым карточкам (27 вместо 2).
        // Пустое действие с приоритетом ниже обычного выполняется после всех уже поставленных в очередь правок карточек.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        result.Skipped.AddRange(skipped.Select(s => new BulkBarcodeGenerator.Skipped(s.Product, s.Reason)));
        _genResult = result;
        RenderPage();
        UpdateSelectionUi();
        UpdateGenerateUi();
        SetStatus(T($"Создано {result.Created.Count:N0}, пропущено {result.Skipped.Count:N0}, ошибок {result.Failed.Count:N0}.", $"Түзүлдү {result.Created.Count:N0}, өткөрүлдү {result.Skipped.Count:N0}, ката {result.Failed.Count:N0}.", $"Created {result.Created.Count:N0}, skipped {result.Skipped.Count:N0}, errors {result.Failed.Count:N0}.", $"Oluşturuldu {result.Created.Count:N0}, atlandı {result.Skipped.Count:N0}, hata {result.Failed.Count:N0}.", $"Yaratildi {result.Created.Count:N0}, o'tkazib yuborildi {result.Skipped.Count:N0}, xato {result.Failed.Count:N0}.")
            + (result.Cancelled ? " " + T("Остановлено.", "Токтотулду.", "Stopped.", "Durduruldu.", "To'xtatildi.") : "")
            + (result.Failed.Count > 0 ? " " + T("Подробности — «Скопировать журнал».", "Чоо-жайы — «Журналды көчүрүү».", "Details — “Copy the log”.", "Ayrıntılar — «Günlüğü kopyala».", "Tafsilotlar — «Jurnalni nusxalash».") : ""));
    }

    /// <summary>2026-11-12 (Л-16): оценка времени в подтверждении — проход идёт со скоростью не больше <see cref="BulkBarcodeGenerator.RequestsPerSecond"/> товаров в секунду.</summary>
    private static string EstimateText(int count)
    {
        var seconds = BulkBarcodeGenerator.EstimateSeconds(count); // 2026-11-13: расчёт вынесен из окна
        var minutes = BulkBarcodeGenerator.EstimateMinutes(count);
        var time = seconds < 90 ? T($"{seconds} с", $"{seconds} сек", $"{seconds} s", $"{seconds} sn", $"{seconds} s")
            : T($"около {minutes:N0} мин", $"болжол менен {minutes:N0} мүнөт", $"about {minutes:N0} min", $"yaklaşık {minutes:N0} dk", $"taxminan {minutes:N0} daqiqa");
        return T($"Это займёт {time}; печать и выбор в это время доступны, есть кнопка «Остановить».", $"Бул {time} алат; бул убакта басуу жана тандоо жеткиликтүү, «Токтотуу» баскычы бар.", $"This will take {time}; printing and selection stay available, there is a Stop button.", $"Bu {time} sürer; bu sırada yazdırma ve seçim açıktır, «Durdur» düğmesi var.", $"Bu {time} oladi; shu vaqtda chop etish va tanlash mavjud, «To'xtatish» tugmasi bor.");
    }

    private void PrintCreated()
    {
        if (_genResult is not { Created.Count: > 0 } r)
            return;
        ApplyPreset(r.Created.Select(c => (c.Product.Id, (string?)c.Code, 0)).ToList());
        SetStatus(T($"Созданные ({r.Created.Count:N0}) отмечены — нажмите «Печать».", $"Түзүлгөндөр ({r.Created.Count:N0}) белгиленди — «Басуу» баскычын басыңыз.", $"The created ({r.Created.Count:N0}) are selected — press Print.", $"Oluşturulanlar ({r.Created.Count:N0}) seçildi — «Yazdır»a basın.", $"Yaratilganlar ({r.Created.Count:N0}) belgilandi — «Chop etish»ni bosing."));
    }

    private async Task CopyLogAsync()
    {
        if (_genResult is null)
            return;
        var text = BulkBarcodeGenerator.BuildLog(_genResult);
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(text).ConfigureAwait(true);
            SetStatus(T("Журнал скопирован в буфер обмена.", "Журнал алмашуу буферине көчүрүлдү.", "The log was copied to the clipboard.", "Günlük panoya kopyalandı.", "Jurnal buferga nusxalandi."));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Массовая генерация штрихкодов: журнал не скопирован ({ex.Message}).", "WARNING");
        }
    }
}
