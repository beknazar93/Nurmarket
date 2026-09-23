using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Threading;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>Avalonia port: disk cache + download for product thumbnails (path-based, no WPF ImageSource).</summary>
internal sealed class ProductThumbService
{
    private static readonly HttpClient PublicHttp = new() { Timeout = TimeSpan.FromSeconds(25) };
    private readonly string _cacheDir;
    private readonly ConcurrentDictionary<string, Task<string?>> _downloadTasks = new(StringComparer.OrdinalIgnoreCase);

    public ProductThumbService()
    {
        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NurMarketKassa",
            "product_thumbs");
        try
        {
            Directory.CreateDirectory(_cacheDir);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Product thumbnail cache creation failed: {ex.GetType().Name}", "WARNING");
        }
    }

    public async Task SetThumbAsync(
        Dispatcher uiDispatcher,
        IAuthApiService authApi,
        string apiBaseUrl,
        string imageUrl,
        Models.Pos.CatalogProductTileVm vm,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(vm.ProductImagePath))
            return;

        var path = await GetOrDownloadPathAsync(authApi, apiBaseUrl, imageUrl, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        await uiDispatcher.InvokeAsync(() => vm.ProductImagePath = path);
    }

    private async Task<string?> GetOrDownloadPathAsync(
        IAuthApiService authApi,
        string apiBaseUrl,
        string imageUrl,
        CancellationToken ct,
        bool forceDownload = false)
    {
        var key = Sha256Hex(imageUrl);
        var ext = GuessExt(imageUrl);
        var local = Path.Combine(_cacheDir, key + ext);
        if (!forceDownload && File.Exists(local))
            return local;

        var task = _downloadTasks.GetOrAdd(local, _ => DownloadToCacheAsync(local, authApi, apiBaseUrl, imageUrl, ct));
        try { return await task.ConfigureAwait(false); }
        finally { _downloadTasks.TryRemove(local, out _); }
    }

    private static async Task<string?> DownloadToCacheAsync(
        string local,
        IAuthApiService authApi,
        string apiBaseUrl,
        string imageUrl,
        CancellationToken ct)
    {
        try
        {
            var uri = BuildAbsoluteUri(apiBaseUrl, imageUrl);
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            var token = authApi.AccessToken;
            if (!string.IsNullOrWhiteSpace(token))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var resp = await PublicHttp.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length == 0)
                return null;

            await File.WriteAllBytesAsync(local, bytes, ct).ConfigureAwait(false);
            return local;
        }
        catch
        {
            return null;
        }
    }

    private static Uri BuildAbsoluteUri(string apiBaseUrl, string imageUrl)
    {
        if (Uri.TryCreate(imageUrl, UriKind.Absolute, out var abs))
            return abs;
        var baseUri = new Uri(apiBaseUrl.EndsWith('/') ? apiBaseUrl : apiBaseUrl + "/", UriKind.Absolute);
        return new Uri(baseUri, imageUrl.TrimStart('/'));
    }

    private static string GuessExt(string url)
    {
        try
        {
            var path = new Uri(url, UriKind.RelativeOrAbsolute).IsAbsoluteUri
                ? new Uri(url).AbsolutePath
                : url;
            var ext = Path.GetExtension(path);
            return string.IsNullOrWhiteSpace(ext) ? ".jpg" : ext;
        }
        catch { return ".jpg"; }
    }

    private static string Sha256Hex(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
