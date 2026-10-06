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

    private sealed record Release(Version Version, string Text, string ApkUrl, string? Notes, bool Prerelease);

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
            if (r.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                    if ((a.GetProperty("name").GetString() ?? "").EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                    {
                        apk = a.GetProperty("browser_download_url").GetString();
                        break;
                    }
            if (apk is null)
                continue;
            list.Add(new Release(Norm(version), text, apk, r.TryGetProperty("body", out var b) ? b.GetString() : null,
                r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True));
        }
        return list.OrderByDescending(x => x.Version).ToList();
    }

    public async Task<AppUpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var current = CurrentVersion();
        try
        {
            var releases = await LoadReleasesAsync(cancellationToken).ConfigureAwait(false);
            var latest = releases.FirstOrDefault(r => !r.Prerelease);
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
            }
        }
        catch
        {
            // не удалось — не страшно
        }
        var request = new DownloadManager.Request(Android.Net.Uri.Parse(release.ApkUrl));
        request.SetTitle("NurMarket " + release.Text);
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
                    "разрешите NurMarket устанавливать приложения (переключатель на открывшемся экране), вернитесь и нажмите «Обновить» ещё раз",
                    "NurMarket'ке тиркемелерди орнотууга уруксат бериңиз (ачылган экрандагы которгуч), кайтып, «Жаңыртуу» дагы басыңыз",
                    "allow NurMarket to install apps (the switch on the screen that opened), come back and press “Update” again",
                    "NurMarket'in uygulama yüklemesine izin verin (açılan ekrandaki anahtar), geri dönüp «Güncelle»ye tekrar basın",
                    "NurMarket'ga ilovalarni o'rnatishga ruxsat bering (ochilgan ekrandagi tugma), qaytib, «Yangilash»ni yana bosing"));
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
