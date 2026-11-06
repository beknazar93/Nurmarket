using System.Text.Json;
using Android.App;
using Android.Content;
using Android.OS;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.Droid;

/// <summary>
/// 2026-10-05, владелец (снимок с планшета): «баг» — в «Настройки → Обновления» на Android было «Проверка обновлений не
/// настроена (не задан адрес манифеста)»: проверка обновлений Windows-кассы (Velopack) на Android не работает.
/// Здесь своя: выпуски берутся с GitHub (beknazar93/Nurmarket-Android, тег android-X.Y.Z, файл .apk), APK скачивает
/// системный загрузчик Android (с процентами и в шторке), установку открывает обычное окно Android — поверх
/// прежней версии, вход и данные сохраняются. Разрешение «установка из этого источника» Android спросит один раз.
/// Откат на старую версию Android не позволяет (меньший versionCode) — список версий только для просмотра.
/// </summary>
internal sealed class AndroidApkUpdateService : IAppUpdateService
{
    private const string Repo = "beknazar93/Nurmarket-Android";
    private static readonly HttpClient Http = CreateHttp();
    private Release? _pending;
    private long _downloadId = -1;

    // 2026-11-05 (аудит №3, А3-12): Size и Sha256 — из ответа GitHub (поле digest «sha256:…»); проверяются после загрузки, до установки.
    private sealed record Release(Version Version, string Text, string ApkUrl, string? Notes, bool Prerelease, long Size = 0, string? Sha256 = null);

    public bool HasPendingUpdate => _pending is not null;

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NurMarketKassa-Android");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Версия без четвёртого числа: 1.17.52.0 и 1.17.52 — одна версия.</summary>
    private static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static Version CurrentVersion()
    {
        try
        {
            var ctx = Android.App.Application.Context;
#pragma warning disable CA1422, CS0618 // GetPackageInfo(string, int) устарел с Android 13, но работает на всех версиях
            var name = ctx.PackageManager?.GetPackageInfo(ctx.PackageName!, 0)?.VersionName;
#pragma warning restore CA1422, CS0618
            if (Version.TryParse(name, out var v))
                return Norm(v);
        }
        catch
        {
            // ниже — версия сборки
        }
        return Norm(typeof(AndroidApkUpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0));
    }

    private static async Task<List<Release>> LoadReleasesAsync(CancellationToken ct)
    {
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases?per_page=30", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var list = new List<Release>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                continue;
            var tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var text = tag.StartsWith("android-", StringComparison.OrdinalIgnoreCase) ? tag[8..] : tag.TrimStart('v');
            // Тестовые сборки вида 1.17.44-test1 — не для клиентов.
            if (!Version.TryParse(text, out var version))
                continue;
            string? apk = null;
            long apkSize = 0;
            string? apkSha = null;
            if (r.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                    if ((a.GetProperty("name").GetString() ?? "").EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                    {
                        apk = a.GetProperty("browser_download_url").GetString();
                        if (a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number)
                            apkSize = sz.GetInt64();
                        var digest = a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() : null;
                        if (digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                            apkSha = digest[7..].Trim().ToLowerInvariant();
                        break;
                    }
            // 2026-11-05 (А3-12): ссылка на APK — только https с GitHub (api.github.com отдаёт browser_download_url на github.com)
            if (apk is null || !IsTrustedApkUrl(apk))
                continue;
            list.Add(new Release(Norm(version), text, apk, r.TryGetProperty("body", out var b) ? b.GetString() : null,
                r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True, apkSize, apkSha));
        }
        return list.OrderByDescending(x => x.Version).ToList();
    }

    private static bool IsTrustedApkUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
        && (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>2026-11-05 (А3-12): скачанный APK — сверка размера и SHA-256 с данными GitHub до установки. Не сошлось — файл удаляется,
    /// установка не открывается. Если GitHub не отдал размер/digest — проверяем только то, что есть.</summary>
    private static void VerifyDownloaded(Release release, string fileName)
    {
        var ctx = Android.App.Application.Context;
        var dir = ctx.GetExternalFilesDir(Android.OS.Environment.DirectoryDownloads);
        if (dir is null)
            return;
        var path = System.IO.Path.Combine(dir.AbsolutePath, fileName);
        if (!System.IO.File.Exists(path))
            return;
        string? problem = null;
        var actualSize = new System.IO.FileInfo(path).Length;
        if (release.Size > 0 && actualSize != release.Size)
            problem = $"размер {actualSize} вместо {release.Size}";
        else if (release.Sha256 is not null)
        {
            using var stream = System.IO.File.OpenRead(path);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
            if (hash != release.Sha256)
                problem = "контрольная сумма SHA-256 не совпала";
        }
        if (problem is null)
            return;
        try { System.IO.File.Delete(path); } catch { /* не удалось — не страшно */ }
        PosLogger.Log($"Обновление Android: {release.Text} — {problem}; файл удалён.", "WARNING");
        throw new InvalidOperationException(Tr.T(
            "скачанный файл повреждён (не совпала контрольная сумма) — нажмите «Проверить обновления» и скачайте заново",
            "жүктөлгөн файл бузулган (текшерүү суммасы дал келген жок) — «Жаңыртууларды текшерүү» басып, кайра жүктөңүз",
            "the downloaded file is damaged (checksum mismatch) — press “Check for updates” and download again",
            "indirilen dosya bozuk (sağlama toplamı uyuşmuyor) — «Güncellemeleri kontrol et»e basıp yeniden indirin",
            "yuklangan fayl buzilgan (nazorat summasi mos kelmadi) — «Yangilanishlarni tekshirish»ni bosib, qayta yuklab oling"));
    }

    public async Task<AppUpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var current = CurrentVersion();
        try
        {
            var releases = await LoadReleasesAsync(cancellationToken).ConfigureAwait(false);
            // 2026-10-21, тестовый выпуск 1.17.59 «только для тестеров»: как на Windows (VelopackUpdateService,
            // GithubSource prerelease: tester) — тестовые (pre-release) версии берёт только касса с кодом тестировщика
            // (UpdateChannel.IsTester), обычная касса по-прежнему видит лишь выпущенные. Раньше Android не брал
            // pre-release никогда — тестировщик на планшете не мог получить тестовую версию кнопкой «Обновить».
            var tester = UpdateChannel.IsTester;
            var latest = releases.FirstOrDefault(r => tester || !r.Prerelease);
            var newer = latest is not null && latest.Version > current;
            _pending = newer ? latest : null;
            var testing = releases.FirstOrDefault(r => r.Prerelease && r.Version > current && (latest is null || r.Version > latest.Version));
            PosLogger.Log($"Обновление Android: установлена {current}, на GitHub {latest?.Text ?? "—"}{(newer ? " — есть новая" : "")}.", "INFO");
            return new AppUpdateCheckResult(true, newer, current.ToString(), latest?.Text, null, newer ? null : testing?.Text);
        }
        catch (Exception ex) when (ex is not System.OperationCanceledException)
        {
            PosLogger.Log($"Обновление Android: проверка не удалась ({ex.Message}).", "WARNING");
            return new AppUpdateCheckResult(true, false, current.ToString(), null, ex.Message);
        }
    }

    public async Task DownloadAsync(Action<int> onProgress, CancellationToken cancellationToken = default)
    {
        var release = _pending ?? throw new InvalidOperationException(Tr.T("сначала нажмите «Проверить обновления»", "адегенде «Жаңыртууларды текшерүү» басыңыз",
            "press “Check for updates” first", "önce «Güncellemeleri kontrol et»e basın", "avval «Yangilanishlarni tekshirish»ni bosing"));
        var ctx = Android.App.Application.Context;
        var manager = (DownloadManager)ctx.GetSystemService(Context.DownloadService)!;
        var name = $"nurmarket-kassa-android-{release.Text}.apk";
        try
        {
            // Прежний файл с тем же именем — удалить, иначе загрузчик допишет «-1» к имени.
            var dir = ctx.GetExternalFilesDir(Android.OS.Environment.DirectoryDownloads);
            if (dir is not null)
            {
                var old = new Java.IO.File(dir, name);
                if (old.Exists())
                    old.Delete();
                // 2026-11-06: В4 А5-11 — старые APK прошлых обновлений (~120 МБ каждый) раньше копились: перед загрузкой удаляем все nurmarket-kassa-android-*.apk.
                DeleteOldApks(dir);
            }
        }
        catch
        {
            // не удалось — не страшно
        }
        var request = new DownloadManager.Request(Android.Net.Uri.Parse(release.ApkUrl));
        request.SetTitle("NurCRM " + release.Text);
        request.SetMimeType("application/vnd.android.package-archive");
        request.SetNotificationVisibility(DownloadVisibility.Visible);
        request.SetDestinationInExternalFilesDir(ctx, Android.OS.Environment.DirectoryDownloads, name);
        _downloadId = manager.Enqueue(request);
        PosLogger.Log($"Обновление Android: загрузка {release.Text} начата.", "INFO");
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                manager.Remove(_downloadId);
                cancellationToken.ThrowIfCancellationRequested();
            }
            using (var cursor = manager.InvokeQuery(new DownloadManager.Query().SetFilterById(_downloadId)))
            {
                if (cursor is not null && cursor.MoveToFirst())
                {
                    var status = (DownloadStatus)cursor.GetInt(cursor.GetColumnIndex(DownloadManager.ColumnStatus));
                    var total = cursor.GetLong(cursor.GetColumnIndex(DownloadManager.ColumnTotalSizeBytes));
                    var done = cursor.GetLong(cursor.GetColumnIndex(DownloadManager.ColumnBytesDownloadedSoFar));
                    if (total > 0)
                        onProgress((int)Math.Min(100, done * 100 / total));
                    if (status == DownloadStatus.Successful)
                    {
                        onProgress(100);
                        PosLogger.Log($"Обновление Android: {release.Text} скачана ({total / 1048576.0:0.0} МБ).", "INFO");
                        await Task.Run(() => VerifyDownloaded(release, name), cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    if (status == DownloadStatus.Failed)
                    {
                        var reason = cursor.GetInt(cursor.GetColumnIndex(DownloadManager.ColumnReason));
                        throw new InvalidOperationException(Tr.T($"загрузка не удалась (код {reason})", $"жүктөө ишке ашкан жок (код {reason})",
                            $"download failed (code {reason})", $"indirme başarısız (kod {reason})", $"yuklab olish muvaffaqiyatsiz (kod {reason})"));
                    }
                }
            }
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>2026-11-06: В4 А5-11 — удалить все скачанные ранее APK обновлений в папке загрузок программы (и недокачанные «.apk-1»).</summary>
    private static void DeleteOldApks(Java.IO.File dir)
    {
        try
        {
            foreach (var file in dir.ListFiles() ?? Array.Empty<Java.IO.File>())
            {
                var fileName = file.Name ?? "";
                if (fileName.StartsWith("nurmarket-kassa-android-", StringComparison.OrdinalIgnoreCase) && fileName.Contains(".apk", StringComparison.OrdinalIgnoreCase))
                {
                    var size = file.Length();
                    if (file.Delete())
                        PosLogger.Log($"Обновление Android: удалён старый файл {fileName} ({size / 1048576} МБ).", "INFO");
                }
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Обновление Android: чистка старых APK не удалась ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>2026-11-06: В4 А5-11 — при запуске: APK не новее установленной версии уже ни к чему (обновление применено) — удаляем.
    /// Новее установленной (скачана, но ещё не поставлена) оставляем.</summary>
    public static void CleanupDownloads()
    {
        try
        {
            var ctx = Android.App.Application.Context;
            var dir = ctx.GetExternalFilesDir(Android.OS.Environment.DirectoryDownloads);
            if (dir is null)
                return;
            var current = CurrentVersion();
            foreach (var file in dir.ListFiles() ?? Array.Empty<Java.IO.File>())
            {
                var fileName = file.Name ?? "";
                if (!fileName.StartsWith("nurmarket-kassa-android-", StringComparison.OrdinalIgnoreCase))
                    continue;
                var text = fileName["nurmarket-kassa-android-".Length..];
                var end = text.IndexOf(".apk", StringComparison.OrdinalIgnoreCase);
                var parsed = end > 0 && Version.TryParse(text[..end], out var v) ? v : null;
                if (parsed is not null && parsed <= current)
                    file.Delete();
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Обновление Android: чистка при запуске — {ex.Message}", "WARNING");
        }
    }

    /// <summary>Открыть установку скачанного APK. Если Android ещё не разрешил кассе ставить приложения —
    /// сначала экран этого разрешения (после него — «Обновить» ещё раз). Не получилось — APK в браузере.</summary>
    public void ApplyUpdateAndRestart()
    {
        var ctx = Android.App.Application.Context;
        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O && ctx.PackageManager?.CanRequestPackageInstalls() == false)
            {
                var allow = new Intent(Android.Provider.Settings.ActionManageUnknownAppSources, Android.Net.Uri.Parse("package:" + ctx.PackageName));
                allow.AddFlags(ActivityFlags.NewTask);
                ctx.StartActivity(allow);
                PosLogger.Log("Обновление Android: открыт экран разрешения установки.", "INFO");
                throw new InvalidOperationException(Tr.T(
                    "разрешите NurCRM устанавливать приложения (переключатель на открывшемся экране), вернитесь и нажмите «Обновить» ещё раз",
                    "NurCRM'ке тиркемелерди орнотууга уруксат бериңиз (ачылган экрандагы которгуч), кайтып, «Жаңыртуу» дагы басыңыз",
                    "allow NurCRM to install apps (the switch on the screen that opened), come back and press “Update” again",
                    "NurCRM'in uygulama yüklemesine izin verin (açılan ekrandaki anahtar), geri dönüp «Güncelle»ye tekrar basın",
                    "NurCRM'ga ilovalarni o'rnatishga ruxsat bering (ochilgan ekrandagi tugma), qaytib, «Yangilash»ni yana bosing"));
            }
            var manager = (DownloadManager)ctx.GetSystemService(Context.DownloadService)!;
            var uri = manager.GetUriForDownloadedFile(_downloadId)
                      ?? throw new InvalidOperationException(Tr.T("файл обновления не найден", "жаңыртуу файлы табылган жок", "the update file was not found", "güncelleme dosyası bulunamadı", "yangilanish fayli topilmadi"));
            var install = new Intent(Intent.ActionView);
            install.SetDataAndType(uri, "application/vnd.android.package-archive");
            install.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
            ctx.StartActivity(install);
            PosLogger.Log($"Обновление Android: открыта установка {_pending?.Text}.", "INFO");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Обновление Android: установка не открылась ({ex.Message}) — открываю APK в браузере.", "WARNING");
            if (_pending is not null)
            {
                var browser = new Intent(Intent.ActionView, Android.Net.Uri.Parse(_pending.ApkUrl));
                browser.AddFlags(ActivityFlags.NewTask);
                ctx.StartActivity(browser);
            }
        }
    }

    public async Task<IReadOnlyList<AppReleaseVersion>> ListVersionsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var current = CurrentVersion();
            var releases = await LoadReleasesAsync(cancellationToken).ConfigureAwait(false);
            return releases.Where(r => !r.Prerelease).Select(r => new AppReleaseVersion(r.Text, r.Notes, r.Version == current)).ToList();
        }
        catch (Exception ex) when (ex is not System.OperationCanceledException)
        {
            PosLogger.Log($"Обновление Android: список версий не получен ({ex.Message}).", "WARNING");
            return Array.Empty<AppReleaseVersion>();
        }
    }

    /// <summary>Android не ставит версию старше установленной поверх неё — откат невозможен.</summary>
    public bool PrepareRollback(string version) => false;

    public async Task<string?> GetReleaseNotesAsync(string version, CancellationToken cancellationToken = default)
    {
        try
        {
            var releases = await LoadReleasesAsync(cancellationToken).ConfigureAwait(false);
            return releases.FirstOrDefault(r => r.Text == version)?.Notes;
        }
        catch (Exception ex) when (ex is not System.OperationCanceledException)
        {
            return null;
        }
    }
}
