using System.Net.Http;
using System.Reflection;
using System.Text;

namespace NurMarketKassa.Services;

/// <summary>
/// Копит отчёты о необработанных ошибках локально в
/// <c>%LocalAppData%\NurMarketKassa\CrashReports\</c>, чтобы их можно было забрать
/// вручную или отправить, как только появится адрес приёма (<see cref="UploadEndpoint"/>).
/// Пока адрес не задан — отчёты только накапливаются на диске, никуда не уходят.
/// </summary>
public static class CrashReportService
{
    private static readonly string ReportsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        NurMarketKassa.Services.AppMode.DataFolderName, "CrashReports");

    /// <summary>
    /// URL, на который отправлять накопленные отчёты. Заполняется в будущем обновлении —
    /// пока null/пусто, <see cref="TryUploadPendingReportsAsync"/> ничего не делает.
    /// </summary>
    public static string? UploadEndpoint { get; set; }

    /// <summary>Пишет один отчёт об ошибке на диск. Не бросает исключений сама.</summary>
    public static string? WriteReport(Exception exception, string context)
    {
        try
        {
            Directory.CreateDirectory(ReportsDirectory);
            var fileName = $"crash_{DateTime.Now:yyyyMMdd_HHmmss_fff}.txt";
            var path = Path.Combine(ReportsDirectory, fileName);

            var appVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "?";
            var report = new StringBuilder()
                .AppendLine($"Время: {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
                .AppendLine($"Контекст: {context}")
                .AppendLine($"Версия приложения: {appVersion}")
                .AppendLine($"ОС: {Environment.OSVersion}, .NET {Environment.Version}")
                .AppendLine(new string('-', 60))
                .AppendLine(SensitiveDataRedactor.Redact(exception.ToString()));

            File.WriteAllText(path, report.ToString());
            return path;
        }
        catch (Exception writeEx)
        {
            PosLogger.Log($"Failed to write crash report: {writeEx.GetType().Name}", "WARNING");
            return null;
        }
    }

    public static int PendingReportCount()
    {
        try
        {
            return Directory.Exists(ReportsDirectory)
                ? Directory.GetFiles(ReportsDirectory, "crash_*.txt").Length
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Отправляет накопленные локальные отчёты, если <see cref="UploadEndpoint"/> уже настроен.
    /// Успешно отправленные файлы удаляются; при ошибке остаются для следующей попытки.
    /// </summary>
    public static async Task TryUploadPendingReportsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(UploadEndpoint) || !Directory.Exists(ReportsDirectory))
            return;

        string[] files;
        try
        {
            files = Directory.GetFiles(ReportsDirectory, "crash_*.txt");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Crash report scan failed: {ex.GetType().Name}", "WARNING");
            return;
        }

        if (files.Length == 0)
            return;

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        foreach (var file in files)
        {
            try
            {
                var text = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                using var content = new StringContent(text);
                var response = await client.PostAsync(UploadEndpoint, content, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    File.Delete(file);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Crash report upload failed for {Path.GetFileName(file)}: {ex.GetType().Name}", "WARNING");
            }
        }
    }
}
