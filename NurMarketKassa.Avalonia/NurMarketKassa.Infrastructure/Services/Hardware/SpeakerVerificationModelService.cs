using System.IO.Compression;
using System.Net.Http;

namespace NurMarketKassa.Services.Hardware;

/// <summary>
/// Модель распознавания ГОЛОСА (чей это голос, а не что сказано) для "голосового замка"
/// (2026-09-05) — отдельная доп. услуга поверх голосового управления: WeSpeaker ResNet34-LM
/// (Apache 2.0, обучена на VoxCeleb, ~26 МБ), запускается через sherpa-onnx
/// (SpeakerEmbeddingExtractor/SpeakerEmbeddingManager) — та же схема, что и модели Vosk
/// (VoiceModelDownloadService): не входит в базовую установку, скачивается отдельно после
/// разблокировки в %AppData%/NurMarketKassa/VoiceLockModel (до 2026-09-29 — в папке программы).
/// </summary>
public static class SpeakerVerificationModelService
{
    private const string FileName = "wespeaker_en_voxceleb_resnet34_LM.onnx";
    // 2026-09-25: раньше модель лежала архивом в нашем выпуске v1.16.1 на GitHub; выпуск удалён,
    // и скачивание падало с 404 («Не удалось скачать модуль голосового замка»). Теперь файл
    // берётся напрямую у первоисточника — выпуск моделей sherpa-onnx (так у них и называется,
    // с опечаткой «recongition»), он не зависит от чистки наших выпусков.
    private const string DownloadUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/wespeaker_en_voxceleb_resnet34_LM.onnx";
    public const int ApproxSizeMb = 27;

    /// <summary>2026-09-29: модель и голос кассира (voiceprint.json) — в %AppData%, рядом с моделями
    /// Vosk (VoiceModelDownloadService). Раньше они лежали в папке программы (current\VoiceLockModel),
    /// а её каждое обновление заменяет целиком: после обновления модель пропадала, голос кассира —
    /// тоже, и голосовой замок молча переставал что-либо проверять (Verify без голоса = null).</summary>
    public static string ModelDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        NurMarketKassa.Services.AppMode.DataFolderName, "VoiceLockModel");

    private static string LegacyModelDir => Path.Combine(AppContext.BaseDirectory, "VoiceLockModel");
    public static string ModelPath => Path.Combine(ModelDir, FileName);

    /// <summary>Переносит модель и голос кассира из старой папки внутри программы — если они там
    /// ещё есть (касса, поставленная копированием, или первый запуск после этой правки).</summary>
    public static void MigrateLegacyFiles()
    {
        try
        {
            if (!Directory.Exists(LegacyModelDir))
                return;
            foreach (var source in Directory.GetFiles(LegacyModelDir))
            {
                if (source.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                    continue;
                var target = Path.Combine(ModelDir, Path.GetFileName(source));
                if (File.Exists(target))
                    continue;
                Directory.CreateDirectory(ModelDir);
                File.Copy(source, target);
                PosLogger.Log($"Голосовой замок: {Path.GetFileName(source)} перенесён в {ModelDir}.", "VOICE_LOCK");
            }
        }
        catch (Exception ex)
        {
            // Перенос — удобство: не вышло, модель скачается заново, голос запишется заново.
            PosLogger.Log($"Voice lock files migration skipped: {ex.GetType().Name}: {ex.Message}", "WARNING");
        }
    }

    public static bool IsInstalled()
    {
        try
        {
            MigrateLegacyFiles();
            return File.Exists(ModelPath) && new FileInfo(ModelPath).Length > 1_000_000;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Voice lock model check failed: {ex.GetType().Name}", "WARNING");
            return false;
        }
    }

    public static async Task<bool> DownloadAndInstallAsync(IProgress<double>? progress, CancellationToken ct = default)
    {
        var tempPath = Path.Combine(ModelDir, $"{FileName}.{Guid.NewGuid():N}.part");
        try
        {
            Directory.CreateDirectory(ModelDir);

            using (var http = new HttpClient())
            {
                using var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                await using var httpStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fileStream = new FileStream(
                    tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                var buffer = new byte[81920];
                long readTotal = 0;
                int read;
                while ((read = await httpStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    readTotal += read;
                    if (totalBytes > 0)
                        progress?.Report(Math.Min(99.0, (double)readTotal / totalBytes * 100));
                }
            }

            if (new FileInfo(tempPath).Length < 1_000_000)
                throw new InvalidDataException("Скачанный файл модели слишком мал — вероятно, это страница ошибки, а не модель.");

            progress?.Report(99.5);
            File.Move(tempPath, ModelPath, overwrite: true);
            progress?.Report(100);

            PosLogger.Log("Модель голосового замка скачана и установлена.", "VOICE_LOCK");
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Voice lock model download failed: {ex}", "ERROR");
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Voice lock model temp file cleanup failed: {ex.GetType().Name}", "WARNING");
            }
        }
    }
}
