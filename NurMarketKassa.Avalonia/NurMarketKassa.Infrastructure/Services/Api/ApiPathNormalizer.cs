using System.Diagnostics;
using System.Net.Http;

namespace NurMarketKassa.Services.Api;

/// <summary>
/// Django APPEND_SLASH: mutating-запросы должны иметь «/» в конце пути (не query).
/// </summary>
internal static class ApiPathNormalizer
{
    private static readonly Uri PlaceholderBase = new("https://api.local/", UriKind.Absolute);

    public static string EnsureTrailingSlash(string relativePath, HttpMethod method)
    {
        if (!RequiresTrailingSlash(method) || string.IsNullOrWhiteSpace(relativePath))
            return relativePath;

        var original = relativePath;
        var trimmed = relativePath.TrimStart('/');

        var pathOnly = trimmed;
        var querySuffix = "";
        var qIndex = trimmed.IndexOf('?', StringComparison.Ordinal);
        if (qIndex >= 0)
        {
            pathOnly = trimmed[..qIndex];
            querySuffix = trimmed[qIndex..];
        }

        if (pathOnly.Length == 0)
            return original;

        var absolute = new Uri(PlaceholderBase, pathOnly);
        var normalizedAbsolute = NormalizeMutatingUri(absolute, method);
        var relative = PlaceholderBase.MakeRelativeUri(normalizedAbsolute).ToString().Replace('\\', '/');
        if (!string.IsNullOrEmpty(querySuffix))
            relative += querySuffix;

        LogMutation(original, relative, method);
        return relative;
    }

    /// <summary>Нормализует только <see cref="UriBuilder.Path"/>; query-строка не затрагивается.</summary>
    public static Uri NormalizeMutatingUri(Uri uri, HttpMethod method)
    {
        if (!RequiresTrailingSlash(method))
            return uri;

        var builder = new UriBuilder(uri);
        var path = builder.Path;
        if (!path.EndsWith('/'))
            builder.Path = path + "/";

        var normalized = builder.Uri;
        if (!UriEquals(normalized, uri))
            PosLogger.Log($"API path normalized: {uri} -> {normalized}", "DEBUG");
        return normalized;
    }

    public static void ApplyToRequest(HttpRequestMessage request)
    {
        if (request.RequestUri == null || !RequiresTrailingSlash(request.Method))
            return;

        request.RequestUri = NormalizeMutatingUri(request.RequestUri, request.Method);
    }

    internal static bool RequiresTrailingSlash(HttpMethod method)
    {
        var m = method.Method;
        return m.Equals("POST", StringComparison.OrdinalIgnoreCase)
               || m.Equals("PUT", StringComparison.OrdinalIgnoreCase)
               || m.Equals("PATCH", StringComparison.OrdinalIgnoreCase)
               || m.Equals("DELETE", StringComparison.OrdinalIgnoreCase);
    }

    private static void LogMutation(string original, string normalized, HttpMethod method)
    {
        if (RequiresTrailingSlash(method) || !string.Equals(original, normalized, StringComparison.Ordinal))
            PosLogger.Log($"API path normalized: {original} -> {normalized}", "DEBUG");
    }

    private static bool UriEquals(Uri left, Uri right) =>
        string.Equals(left.AbsoluteUri, right.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
}
