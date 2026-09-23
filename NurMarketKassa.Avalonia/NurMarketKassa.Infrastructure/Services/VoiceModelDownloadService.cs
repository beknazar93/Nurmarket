using System.IO.Compression;
using System.Net.Http;

namespace NurMarketKassa.Services;

/// <summary>Язык модели распознавания речи для голосового управления.</summary>
public enum VoiceModelLanguage { Russian, Kyrgyz }

/// <summary>
/// Пакеты распознавания речи (Vosk) больше не входят в базовую установку кассы (см.
/// NurMarketKassa.Avalonia.csproj — раньше был "VoiceModel\**" с CopyToPublishDirectory=Always)
/// — теперь это платная доп. услуга (MarketplaceView, "Доп. функции"), скачивается сюда же
/// (AppContext.BaseDirectory/VoiceModel) отдельно, после разблокировки. Русская модель уже была
/// (~113 МБ); кыргызская добавлена по запросу пользователя (2026-09-04) — маленькая модель Vosk
/// для кыргызского языка существует официально (vosk-model-small-ky-0.42, Apache 2.0, ~49 МБ
/// сжатая) и была перезалита тем же способом, что и русская: как отдельный asset в существующем
/// GitHub-релизе v1.16.0. Обе модели можно установить одновременно — VoiceControlService сам
/// выбирает, какую использовать (см. ResolvePreferredModelPath), в первую очередь по языку
/// интерфейса кассы (UserPreferences.Language), с откатом на вторую, если предпочтительная не
/// установлена.
/// </summary>
public static class VoiceModelDownloadService
{
    private static readonly Dictionary<VoiceModelLanguage, (string FolderName, string Url, int ApproxSizeMb)> Models = new()
    {
        // Ссылки ведут на ОФИЦИАЛЬНЫЙ сайт Vosk (Alpha Cephei) — первоисточник этих моделей.
        // Раньше здесь были ссылки на релиз v1.16.0 нашего репозитория, но такого релиза не
        // существует, и файлы туда никогда не выкладывались: оба адреса отдавали 404, поэтому
        // голосовое управление не могло скачаться НИ У КОГО (проверено запросом 2026-09-22).
        // Обе модели свободные, Apache 2.0, и имена папок внутри архивов совпадают с теми,
        // что ждёт ModelDir — распаковка ложится ровно куда надо, без переименований.
        // Размеры — реальные размеры архивов, а не распакованных папок: именно их видит кассир
        // на кнопке (раньше стояло 113 и 60 МБ, что не соответствовало ни тому, ни другому).
        [VoiceModelLanguage.Russian] = (
            "vosk-model-small-ru-0.22",
            "https://alphacephei.com/vosk/models/vosk-model-small-ru-0.22.zip",
            44),
        [VoiceModelLanguage.Kyrgyz] = (
            "vosk-model-small-ky-0.42",
            "https://alphacephei.com/vosk/models/vosk-model-small-ky-0.42.zip",
            49),
    };

    /// <summary>Папка с моделями. ВАЖНО: НЕ рядом с exe. Раньше было
    /// AppContext.BaseDirectory\VoiceModel, то есть внутри ...\NurMarketKassa\current\ —
    /// а эту папку Velopack ЗАМЕНЯЕТ целиком на каждом обновлении. Скачанная модель на 113 МБ
    /// исчезала после первого же автообновления, и кассир качал её заново. %AppData% — то же
    /// место, где уже живёт локальная база (см. DatabaseService), оно переживает обновления.</summary>
    private static string VoiceModelRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NurMarketKassa", "VoiceModel");

    /// <summary>Прежнее расположение — для одноразового переноса уже скачанной модели
    /// у тех, кто успел её поставить до этой правки.</summary>
    private static string LegacyVoiceModelRoot => Path.Combine(AppContext.BaseDirectory, "VoiceModel");

    private static void MigrateLegacyModels()
    {
        try
        {
            if (!Directory.Exists(LegacyVoiceModelRoot))
                return;

            foreach (var source in Directory.GetDirectories(LegacyVoiceModelRoot))
            {
                var name = Path.GetFileName(source);
                var target = Path.Combine(VoiceModelRoot, name);
                if (Directory.Exists(target) || !Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Any())
                    continue;

                Directory.CreateDirectory(VoiceModelRoot);
                Directory.Move(source, target);
                PosLogger.Log($"Модель голосового распознавания перенесена в {target}", "VOICE");
            }
        }
        catch (Exception ex)
        {
            // Перенос — удобство, а не условие работы: не вышло, модель просто скачается заново.
            PosLogger.Log($"Voice model migration skipped: {ex.GetType().Name}: {ex.Message}", "WARNING");
        }
    }
    private static string ModelDir(VoiceModelLanguage language) => Path.Combine(VoiceModelRoot, Models[language].FolderName);

    public static int GetApproxSizeMb(VoiceModelLanguage language) => Models[language].ApproxSizeMb;

    public static bool IsInstalled(VoiceModelLanguage language)
    {
        try
        {
            MigrateLegacyModels();
            var dir = ModelDir(language);
            // EnumerateFileSystemEntries (нерекурсивно) засчитывает и пустые подпапки — сборка
            // (или неудачная/оборванная распаковка) может оставить скелет "am/conf/graph/ivector"
            // без единого файла внутри. Vosk в этом случае не бросает управляемое исключение, а
            // падает нативным AccessViolationException при создании VoskRecognizer — такое
            // исключение необрабатываемо в .NET Core и убивает процесс целиком (2026-09-06,
            // найдено при тестовом запуске свежей сборки). Поэтому здесь обязательно нужен
            // рекурсивный поиск хотя бы одного РЕАЛЬНОГО файла, а не просто записи в каталоге.
            return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Voice model check failed ({language}): {ex.GetType().Name}", "WARNING");
            return false;
        }
    }

    /// <summary>Путь к папке модели для предпочтительного языка — с откатом на второй язык, если
    /// предпочтительный не установлен (например, кассир поставил только русскую модель, но
    /// интерфейс переключён на кыргызский). null, если не установлена ни одна.</summary>
    public static string? ResolvePreferredModelPath(VoiceModelLanguage preferred)
    {
        if (IsInstalled(preferred))
            return ModelDir(preferred);

        var fallback = preferred == VoiceModelLanguage.Russian ? VoiceModelLanguage.Kyrgyz : VoiceModelLanguage.Russian;
        return IsInstalled(fallback) ? ModelDir(fallback) : null;
    }

    /// <summary>Скачивает и ставит модель. Возвращает причину отказа текстом для кассира —
    /// раньше метод отдавал просто false, а окно на ЛЮБУЮ ошибку писало «Проверьте интернет»,
    /// хотя интернет мог быть в полном порядке: например, файла нет на сервере (404) или нет
    /// прав на запись. Владелец из-за этого искал проблему не там.</summary>
    public static async Task<(bool Success, string? Error)> DownloadAndInstallAsync(
        VoiceModelLanguage language, IProgress<double>? progress, CancellationToken ct = default)
    {
        var zipPath = Path.Combine(Path.GetTempPath(), $"nmk-voice-model-{language}-{Guid.NewGuid():N}.zip");
        try
        {
            Directory.CreateDirectory(VoiceModelRoot);

            using (var http = new HttpClient())
            {
                using var response = await http.GetAsync(Models[language].Url, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                await using var httpStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fileStream = new FileStream(
                    zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

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

            progress?.Report(99.5); // распаковка — прогресс-бар не детализируем по файлам.
            ZipFile.ExtractToDirectory(zipPath, VoiceModelRoot, overwriteFiles: true);
            progress?.Report(100);

            PosLogger.Log($"Пакет голосового распознавания ({language}) скачан и установлен.", "VOICE");
            return (true, null);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound
                                              or System.Net.HttpStatusCode.Forbidden)
        {
            // Самый частый случай на практике: ссылка ведёт на релиз, которого нет. Интернет
            // при этом работает — и советовать его проверить бессмысленно.
            PosLogger.Log($"Voice model download failed ({language}), файл недоступен: {ex.StatusCode} {Models[language].Url}", "ERROR");
            return (false, "Пакет распознавания речи не найден на сервере. Это не проблема интернета — обратитесь в поддержку.");
        }
        catch (HttpRequestException ex)
        {
            PosLogger.Log($"Voice model download failed ({language}): {ex}", "ERROR");
            return (false, "Не удалось связаться с сервером. Проверьте интернет и попробуйте снова.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            PosLogger.Log($"Voice model download timed out ({language})", "ERROR");
            return (false, "Загрузка прервалась по таймауту. Проверьте скорость соединения и попробуйте снова.");
        }
        catch (OperationCanceledException)
        {
            return (false, null); // отменил сам кассир — сообщение не нужно
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            PosLogger.Log($"Voice model install failed ({language}): {ex}", "ERROR");
            return (false, "Не удалось записать пакет на диск: нет места или нет прав на папку.");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Voice model download failed ({language}): {ex}", "ERROR");
            return (false, "Не удалось установить пакет распознавания речи: " + ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(zipPath))
                    File.Delete(zipPath);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Voice model temp file cleanup failed: {ex.GetType().Name}", "WARNING");
            }
        }
    }
}
