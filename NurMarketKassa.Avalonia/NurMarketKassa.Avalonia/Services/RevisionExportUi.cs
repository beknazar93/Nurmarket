using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-24, ТЗ «Склад», пакет Р3: окно сохранения, сообщения и защита от двойного нажатия для «Выгрузить» в ревизии.
/// Само окно ревизии (пакет Р2) вызывает <see cref="RunAsync"/> из пунктов «Excel / CSV / PDF» кнопки «Выгрузить ▾», передав снимок
/// строк (собранный на потоке окна). Генерация — на фоновом потоке (<see cref="RevisionExportService.ExportAsync"/>), при закрытии
/// окна выгрузка отменяется. Только кроссплатформенный Avalonia (файл общий с Portable/Android).</summary>
internal static class RevisionExportUi
{
    /// <summary>Одна выгрузка за раз: пока идёт, повторное нажатие ничего не запускает (кнопку окно может отключать по <see cref="IsRunning"/>).</summary>
    private static int _running;

    public static bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <param name="rows">Снимок строк ревизии (все, фильтр объёма применяет сервис).</param>
    /// <param name="setMessage">Строка сообщений окна (ErrorMessage).</param>
    /// <param name="markSuccess">Перекрасить строку сообщений в «готово».</param>
    public static async Task RunAsync(Window owner, RevisionExportFormat format, IReadOnlyList<RevisionExportRow> rows,
        RevisionExportMeta meta, RevisionExportOptions options, Action<string?> setMessage, Action markSuccess)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return;

        using var cts = new CancellationTokenSource();
        EventHandler onClosed = (_, _) => cts.Cancel();
        owner.Closed += onClosed;
        try
        {
            var ext = RevisionExportService.Extension(format);
            var kind = format switch
            {
                RevisionExportFormat.Csv => new FilePickerFileType("CSV") { Patterns = ["*.csv"] },
                RevisionExportFormat.Pdf => new FilePickerFileType("PDF") { Patterns = ["*.pdf"] },
                _ => new FilePickerFileType("Excel") { Patterns = ["*.xlsx"] },
            };
            var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Tr.T("Сохранить ревизию", "Ревизияны сактоо", "Save the stocktake", "Sayımı kaydet", "Reviziyani saqlash"),
                SuggestedFileName = RevisionExportService.SuggestFileName(meta, format, options.Scope),
                DefaultExtension = ext,
                FileTypeChoices = [kind],
            }).ConfigureAwait(true);
            if (file is null)
                return;

            var path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
            {
                setMessage(Tr.T("Не удалось определить путь файла — выберите папку на этом компьютере.", "Файлдын жолун аныктоо мүмкүн болгон жок — ушул компьютердеги папканы тандаңыз.", "Could not determine the file path — choose a folder on this computer.", "Dosya yolu belirlenemedi — bu bilgisayarda bir klasör seçin.", "Fayl yo'lini aniqlab bo'lmadi — shu kompyuterdagi papkani tanlang."));
                return;
            }

            // Progress создан на потоке окна — сообщения приходят туда же.
            var progress = new Progress<string>(text => setMessage(text));
            await RevisionExportService.ExportAsync(path!, format, rows, meta, options, progress, cts.Token).ConfigureAwait(true);

            setMessage(Tr.T($"Ревизия сохранена: {path}", $"Ревизия сакталды: {path}", $"Stocktake saved: {path}", $"Sayım kaydedildi: {path}", $"Reviziya saqlandi: {path}"));
            markSuccess();
        }
        catch (OperationCanceledException)
        {
            PosLogger.Log("Выгрузка ревизии отменена (окно закрыто).", "INFO");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Выгрузка ревизии не удалась: {ex}", "WARNING");
            setMessage(Tr.T("Не удалось сохранить ревизию: ", "Ревизияны сактоо мүмкүн болгон жок: ", "Could not save the stocktake: ", "Sayım kaydedilemedi: ", "Reviziyani saqlab bo'lmadi: ") + ex.Message);
        }
        finally
        {
            owner.Closed -= onClosed;
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
