using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services; // 2026-10-29: PosDialogs (без этого сборка падала)
using NurMarketKassa.AvaloniaHost.Views.Controls;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

// 2026-11-06: В15г — часть класса BulkImportWizardWindow (Load): перенесено из BulkImportWizardWindow.cs без изменений логики.
public partial class BulkImportWizardWindow
{
    private async Task LoadAsync()
    {
        if (_plan is null || _started || _plan.Lines.Count == 0)
            return;
        var feeder = BulkReceivingFeeder.Instance;
        if (feeder.IsClosed)
        {
            _status.Text = feeder.ClosedText;
            return;
        }

        // Цена продажи у товаров склада изменится — спрашиваем (как «Провести» в приёмке).
        var changes = _plan.SalePriceChangeCount;
        if (changes > 0 && !SkipConfirmations)
        {
            var go = await PosConfirmDialog.ShowModalAsync(this,
                T("Цена продажи изменится", "Сатуу баасы өзгөрөт", "Sale prices will change", "Satış fiyatları değişecek", "Sotuv narxlari o'zgaradi"),
                T($"Цена продажи изменится у {changes} товаров склада. Загрузить?", $"Кампадагы {changes} товардын сатуу баасы өзгөрөт. Жүктөйбүзбү?",
                    $"The sale price will change for {changes} stock products. Load?", $"{changes} depo ürününün satış fiyatı değişecek. Yüklensin mi?", $"Ombordagi {changes} mahsulotning sotuv narxi o'zgaradi. Yuklaymizmi?"),
                T("Загрузить", "Жүктөө", "Load", "Yükle", "Yuklash"), T("Вернуться", "Артка", "Go back", "Geri dön", "Orqaga")).ConfigureAwait(true);
            if (!go)
                return;
        }

        // 2026-10-30: «только карточки и цены» — без прихода, свой загрузчик пакетами по 100 (BulkCardsLoader).
        if (_plan.CardsOnly)
        {
            await RunCardsAsync(final: true).ConfigureAwait(true);
            return;
        }
        // 2026-10-30: в режиме прихода доп. штрихкоды пишет тот же загрузчик (до прихода), сам приход ведёт очередь.
        if (BulkCardsLoader.Select(_plan).Count > 0)
        {
            var barcodes = await RunCardsAsync(final: false).ConfigureAwait(true);
            // 2026-10-30 (приёмка 1.17.65): «Остановить» на этапе доп. штрихкодов останавливает всю загрузку, приход в очередь не уходит.
            if (barcodes is null || barcodes.Cancelled || _plan.Lines.Count == 0)
                return;
        }

        // 2026-10-30: повтор того же файла — строки, которые журнал уже знает (приход принят очередью), второй раз не отправляются: иначе остаток прибавился бы дважды.
        IReadOnlyList<ReceivingLineVm> sendLines = _plan.Lines;
        IReadOnlyList<int>? sendRows = _plan.FileRows;
        var journal = _journal;
        if (journal is not null)
        {
            if (journal.ReceiptBatchId is not null)
            {
                _status.Text = ResumeNote();
                return;
            }
            // 2026-11-01 (аудит 10 из 10, С-06): исправленный файл (другой хэш) — новый журнал, и все строки приходовались бы второй раз.
            // Ищем свежий журнал того же прихода, где записано больше половины этих штрихкодов, и спрашиваем: только новые строки или всё заново.
            if (journal.DoneCount == 0 && !SkipConfirmations
                && BulkImportJournal.FindSimilarReceipt(journal.Hash, sendLines.Select(l => l.Barcode).ToList()) is { } similar)
            {
                var loadedAt = similar.Journal.UpdatedAt.ToString("dd.MM.yyyy HH:mm");
                var onlyNew = await PosConfirmDialog.ShowModalAsync(this,
                    T("Похоже, этот файл уже загружали", "Бул файл мурда жүктөлгөн окшойт", "This file seems to be loaded already", "Bu dosya daha önce yüklenmiş gibi", "Bu fayl avval yuklangan ko'rinadi"),
                    T($"{similar.Overlap.Count} из {sendLines.Count} строк уже оприходованы файлом «{similar.Journal.FileName}» ({loadedAt}). Загрузить только новые строки? Если в уже загруженных строках вы поправили количество — внесите разницу вручную.",
                        $"{sendLines.Count} саптын {similar.Overlap.Count} «{similar.Journal.FileName}» файлы менен кирешеленген ({loadedAt}). Жаңы саптарды гана жүктөйбүзбү? Жүктөлгөн саптарда санды оңдосоңуз — айырманы кол менен киргизиңиз.",
                        $"{similar.Overlap.Count} of {sendLines.Count} rows were already received by the file “{similar.Journal.FileName}” ({loadedAt}). Load only the new rows? If you corrected a quantity in the already loaded rows, add the difference by hand.",
                        $"{sendLines.Count} satırdan {similar.Overlap.Count} tanesi «{similar.Journal.FileName}» dosyasıyla ({loadedAt}) zaten girildi. Yalnızca yeni satırlar yüklensin mi? Yüklenmiş satırlarda miktarı düzelttiyseniz farkı elle girin.",
                        $"{sendLines.Count} qatordan {similar.Overlap.Count} tasi «{similar.Journal.FileName}» fayli bilan ({loadedAt}) allaqachon kirim qilingan. Faqat yangi qatorlar yuklansinmi? Yuklangan qatorlarda miqdorni tuzatgan bo'lsangiz, farqni qo'lda kiriting."),
                    T("Только новые", "Жаңылары гана", "Only new", "Yalnızca yeni", "Faqat yangilari"),
                    T("Другое…", "Башка…", "Other…", "Diğer…", "Boshqa…")).ConfigureAwait(true);
                if (onlyNew)
                    journal.MarkDone(similar.Overlap);
                else
                {
                    var all = await PosConfirmDialog.ShowModalAsync(this,
                        T("Загрузить всё заново?", "Баарын кайра жүктөйбүзбү?", "Load everything again?", "Hepsi yeniden yüklensin mi?", "Hammasi qayta yuklansinmi?"),
                        T("Остаток уже загруженных строк прибавится ещё раз.", "Жүктөлгөн саптардын калдыгы дагы бир жолу кошулат.", "The stock of the already loaded rows will be added again.",
                            "Daha önce yüklenen satırların stoku bir kez daha eklenecek.", "Allaqachon yuklangan qatorlarning qoldig'i yana qo'shiladi."),
                        T("Загрузить всё", "Баарын жүктөө", "Load all", "Hepsini yükle", "Hammasini yuklash"), T("Не загружать", "Жүктөбөө", "Do not load", "Yükleme", "Yuklamaslik")).ConfigureAwait(true);
                    if (!all)
                        return;
                }
            }
            if (journal.DoneCount > 0)
            {
                var keep = Enumerable.Range(0, sendLines.Count).Where(i => !journal.IsDone(sendLines[i].Barcode)).ToList();
                sendRows = sendRows is { } fr && fr.Count == sendLines.Count ? keep.Select(i => fr[i]).ToList() : null;
                sendLines = keep.Select(i => sendLines[i]).ToList();
                if (sendLines.Count == 0)
                {
                    _status.Text = ResumeNote();
                    return;
                }
            }
            journal.ReceiptBatchId = "pending";
            journal.ReceiptOrder = sendLines.Select(l => l.Barcode).ToList();
            journal.Total = sendLines.Count;
            journal.Completed = false;
            journal.Save();
        }

        try
        {
            feeder.BatchChanged += OnBatchChanged;
            feeder.BatchFinished += OnBatchFinished;
            _batchId = feeder.Start(sendLines, T($"Импорт: {_fileName}", $"Импорт: {_fileName}", $"Import: {_fileName}", $"İçe aktarma: {_fileName}", $"Import: {_fileName}"), sendRows);
        }
        catch (Exception ex)
        {
            feeder.BatchChanged -= OnBatchChanged;
            feeder.BatchFinished -= OnBatchFinished;
            if (journal is not null)
            {
                journal.ReceiptBatchId = null;
                journal.ReceiptOrder = [];
                journal.Save();
            }
            _status.Text = ServerOutageMonitor.FriendlyError(ex);
            return;
        }

        if (journal is not null)
        {
            journal.ReceiptBatchId = _batchId;
            journal.Save();
        }
        _cancelBtn.IsEnabled = true;
        _cancelBtn.IsVisible = true; // «Остановить загрузку» работает и на приходе в фоне
        _started = true;
        _loadBtn.IsEnabled = false;
        _checkBtn.IsEnabled = false;
        _grid.IsReadOnly = true;
        if (_table is not null)
            _mapping.Remember(_table.Headers); // тот же поставщик в следующий раз — без ручного выбора столбцов
        _status.Text = T($"Загрузка в фоне: 0 из {_plan.Lines.Count} · можно закрыть окно — загрузка продолжится.",
            $"Фондо жүктөө: 0 / {_plan.Lines.Count} · терезени жабууга болот — жүктөө уланат.",
            $"Loading in the background: 0 of {_plan.Lines.Count} · you can close the window, loading continues.",
            $"Arka planda yükleniyor: 0 / {_plan.Lines.Count} · pencereyi kapatabilirsiniz, yükleme sürer.",
            $"Fonda yuklanmoqda: 0 / {_plan.Lines.Count} · oynani yopish mumkin, yuklash davom etadi.");
        _bar.IsVisible = true;
        _bar.Maximum = Math.Max(1, _plan.Lines.Count);
        _bar.Value = 0;
    }

    /// <summary>2026-10-30: загрузка карточек/доп. штрихкодов пакетами по 100: ход «Загрузка N из M», остановка кнопкой, итог
    /// «Создано N, обновлено M, пропущено K, ошибок E». final = true — это вся загрузка (режим «только карточки»).</summary>
    private async Task<BulkCardsResult?> RunCardsAsync(bool final)
    {
        var plan = _plan;
        var api = PosApp.CatalogApi;
        if (plan is null || api is null)
        {
            _status.Text = T("Нет связи с NurCRM — войдите в программу заново.", "NurCRM менен байланыш жок — программага кайра кириңиз.", "No connection to NurCRM — sign in again.", "NurCRM bağlantısı yok — yeniden giriş yapın.", "NurCRM bilan aloqa yo'q — qayta kiring.");
            return null;
        }
        var total = BulkCardsLoader.Select(plan).Count;
        _started = true;
        _loadBtn.IsEnabled = false;
        _checkBtn.IsEnabled = false;
        _grid.IsReadOnly = true;
        _cancelBtn.IsVisible = true;
        _loadControl.Paused = false;
        _pauseBtn.Content = PauseText;
        _pauseBtn.IsVisible = true;
        _bar.IsVisible = true;
        _bar.Maximum = Math.Max(1, total);
        _bar.Value = 0;
        _loadCts = new CancellationTokenSource();
        // 2026-10-30 (скорость загрузки): «Загружено 1 240 из 15 433 · 38 строк/с · осталось ~6 мин»; загрузчик сообщает ход не чаще раза в 120 мс.
        var meter = new RateMeter();
        var progress = new Progress<int>(n =>
        {
            _bar.Value = n;
            if (_loadControl.Paused)
                return; // на паузе строку «Пауза…» не затираем
            _status.Text = T($"Загружено {n:N0} из {total:N0}", $"Жүктөлдү {n:N0} / {total:N0}", $"Loaded {n:N0} of {total:N0}", $"Yüklendi {n:N0} / {total:N0}", $"Yuklandi {n:N0} / {total:N0}")
                + RateEtaText(meter.Feed(n), total - n);
        });
        BulkCardsResult result;
        try
        {
            // 2026-10-30: журнал — только у настоящей загрузки карточек (final); этап доп. штрихкодов перед приходом безопасно повторяется (список объединяется).
            result = await BulkCardsLoader.RunAsync(plan, api, progress, _loadCts.Token, final ? _journal : null, _loadControl).ConfigureAwait(true);
        }
        finally
        {
            _cancelBtn.IsVisible = false;
            _pauseBtn.IsVisible = false;
            _loadControl.Paused = false;
            _bar.IsVisible = false;
        }

        if (_table is not null)
            _mapping.Remember(_table.Headers);
        plan.Recompute();
        foreach (var row in _rows)
            row.RaiseAll();
        UpdateTiles();
        if (final)
        {
            // 2026-10-30: остановили (кнопкой, закрытием окна, 401) — «Остановлено, загружено N из M»; журнал остаётся для «Продолжить загрузку».
            // Всё записано без ошибок — журнал карточек не нужен (повтор и так безопасен: каталог сравнится с файлом).
            if (result.Cancelled)
                _status.Text = result.StoppedText + (result.AuthExpired ? T(". Сессия истекла — войдите заново.", ". Сессия бүттү — кайра кириңиз.", ". The session expired — sign in again.", ". Oturum süresi doldu — yeniden giriş yapın.", ". Sessiya tugadi — qayta kiring.") : "")
                    + T(". Нажмите «Проверить», затем «Продолжить загрузку».", ". «Текшерүү», андан «Жүктөөнү улантуу» басыңыз.", ". Press “Check”, then “Continue loading”.", ". «Kontrol et», sonra «Yüklemeye devam et»e basın.", ". «Tekshirish», so'ng «Yuklashni davom ettirish»ni bosing.");
            else
            {
                _status.Text = T("Готово: ", "Даяр: ", "Done: ", "Tamam: ", "Tayyor: ") + result.SummaryText + T(". Чтобы дозагрузить остальное, нажмите «Проверить» — загруженное будет «без изменений».",
                    ". Калганын жүктөө үчүн «Текшерүү» басыңыз — жүктөлгөнү «өзгөрүүсүз» болот.", ". To load the rest press “Check” — what is loaded will show as “no changes”.",
                    ". Kalanı yüklemek için «Kontrol et»e basın — yüklenenler «değişiklik yok» olur.", ". Qolganini yuklash uchun «Tekshirish»ni bosing — yuklanganlari «o'zgarishsiz» bo'ladi.");
                if (result.Failed == 0)
                {
                    _journal?.Delete();
                    _journal = null;
                    _journalDone = 0;
                }
            }
            _summary.Text = result.SummaryText;
            _started = false; // можно проверить заново и дозагрузить: каталог обновится, загруженное станет «без изменений»
            _checkBtn.IsEnabled = true;
            try
            {
                await CatalogCacheService.RefreshFromApiAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Массовая загрузка: каталог после загрузки не обновлён: {ex.Message}", "WARNING");
            }
        }
        else if (result.Cancelled)
        {
            // 2026-10-30: стоп на этапе доп. штрихкодов — приход не стартует; окно снова доступно для проверки и «Продолжить».
            _status.Text = result.StoppedText + (result.AuthExpired ? T(". Сессия истекла — войдите заново.", ". Сессия бүттү — кайра кириңиз.", ". The session expired — sign in again.", ". Oturum süresi doldu — yeniden giriş yapın.", ". Sessiya tugadi — qayta kiring.") : "");
            _started = false;
            _checkBtn.IsEnabled = true;
            _grid.IsReadOnly = false;
            UpdateTiles();
        }
        else if (result.Failed > 0)
            _status.Text = T("Доп. штрихкоды: ", "Кошумча штрих-коддор: ", "Extra barcodes: ", "Ek barkodlar: ", "Qo'shimcha shtrix-kodlar: ") + result.SummaryText;
        return result;
    }

    private void OnBatchChanged(BulkBatchInfo info)
    {
        if (info.Id != _batchId)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            _bar.Maximum = Math.Max(1, info.TotalLines);
            _bar.Value = info.ProcessedLines;
            // 2026-10-30: + скорость и оценка времени (по времени от постановки пачки; первые секунды не показываем — цифра была бы случайной).
            var secs = (DateTime.Now - info.CreatedAt).TotalSeconds;
            var eta = secs >= 8 && info.ProcessedLines > 0 ? RateEtaText(info.ProcessedLines / secs, info.TotalLines - info.ProcessedLines) : "";
            _status.Text = T($"Загрузка в фоне: {info.ProcessedLines} из {info.TotalLines} · {info.SummaryText}", $"Фондо жүктөө: {info.ProcessedLines} / {info.TotalLines} · {info.SummaryText}",
                $"Loading in the background: {info.ProcessedLines} of {info.TotalLines} · {info.SummaryText}", $"Arka planda yükleniyor: {info.ProcessedLines} / {info.TotalLines} · {info.SummaryText}",
                $"Fonda yuklanmoqda: {info.ProcessedLines} / {info.TotalLines} · {info.SummaryText}") + eta;
        });
    }

    private void OnBatchFinished(BulkBatchInfo info)
    {
        if (info.Id != _batchId)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            _bar.IsVisible = false;
            _cancelBtn.IsVisible = false;
            _journal?.FoldReceipt(info); // 2026-10-30: принятые строки уходят в журнал — повтор файла их не пошлёт
            // 2026-10-30: строки, которые очередь не приняла, помечаются ошибкой с причиной — «Скачать строки с ошибками» их выдаст.
            if (_plan is not null && info.FailedRows.Count > 0)
            {
                var byRow = _plan.Rows.GroupBy(r => r.FileRow).ToDictionary(g => g.Key, g => g.First());
                foreach (var failed in info.FailedRows)
                {
                    if (failed.FileRow is { } fr && byRow.TryGetValue(fr, out var planRow))
                        planRow.SetLoadError(failed.Error, failed.NeedsCheck);
                }
                _plan.Recompute();
                foreach (var row in _rows)
                    row.RaiseAll();
                UpdateTiles();
            }
            if (info.Cancelled && info.NotSent > 0)
            {
                // 2026-10-30: остановили — «Остановлено, загружено N из M»; журнал знает принятое, «Проверить» → «Продолжить загрузку» допишет остальное.
                _status.Text = T($"Остановлено, загружено {info.Received} из {info.TotalLines} · {info.SummaryText}. Нажмите «Проверить», затем «Продолжить загрузку».",
                    $"Токтотулду, жүктөлдү {info.Received} / {info.TotalLines} · {info.SummaryText}. «Текшерүү», андан «Жүктөөнү улантуу» басыңыз.",
                    $"Stopped, loaded {info.Received} of {info.TotalLines} · {info.SummaryText}. Press “Check”, then “Continue loading”.",
                    $"Durduruldu, yüklenen {info.Received} / {info.TotalLines} · {info.SummaryText}. «Kontrol et», sonra «Yüklemeye devam et»e basın.",
                    $"To'xtatildi, yuklandi {info.Received} / {info.TotalLines} · {info.SummaryText}. «Tekshirish», so'ng «Yuklashni davom ettirish»ni bosing.");
                _started = false;
                _checkBtn.IsEnabled = true;
                _grid.IsReadOnly = false;
                return;
            }
            _status.Text = T($"Готово: {info.SummaryText}. Строки, которые не приняты, — в «Приходах в фоне» (можно повторить).", $"Даяр: {info.SummaryText}. Кабыл алынбаган саптар — «Фондогу кирешелерде» (кайталоого болот).",
                $"Done: {info.SummaryText}. Rows that were not accepted are in “Background receipts” (you can retry them).", $"Tamam: {info.SummaryText}. Kabul edilmeyen satırlar «Arka plan girişleri»nde (tekrar denenebilir).",
                $"Tayyor: {info.SummaryText}. Qabul qilinmagan qatorlar «Fondagi kirimlar»da (qayta urinish mumkin).");
        });
    }

}
