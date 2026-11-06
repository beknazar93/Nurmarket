using System;
using System.Diagnostics;
using System.IO;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-06: В4 А5-05 — единое место, где касса открывает ссылку (сайт, WhatsApp, Telegram, tel:) или файл в программе
/// по умолчанию. Раньше в 23 файлах стоял <c>Process.Start(UseShellExecute)</c> — на Android это исключение, на Linux он
/// полагается на внутренний поиск xdg-open. Теперь: Windows — оболочка Windows (как было), Linux/macOS — xdg-open / open через
/// ArgumentList (без склейки кавычек), Android — Intent через обработчики, которые подставляет проект Android при старте
/// (<see cref="AndroidOpenUrl"/>, <see cref="AndroidOpenFile"/>; в общем коде Android API недоступен). Все методы не бросают
/// исключений: возвращают false и пишут в журнал.</summary>
internal static class PlatformLauncher
{
    /// <summary>Android: открыть ссылку Intent'ом (ACTION_VIEW). Подставляет AndroidLauncher.Install.</summary>
    public static Func<string, bool>? AndroidOpenUrl { get; set; }

    /// <summary>Android: открыть файл Intent'ом через FileProvider. Подставляет AndroidLauncher.Install.</summary>
    public static Func<string, bool>? AndroidOpenFile { get; set; }

    /// <summary>Ссылку можно отдавать системе: непустая, без переводов строк, абсолютная (http, https, tg, tel, mailto, whatsapp …).
    /// Относительные пути и «file:» не принимаются — файлы открывает <see cref="OpenFile"/>.</summary>
    public static bool IsLaunchableUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return false;
        return !uri.IsFile && !string.IsNullOrEmpty(uri.Scheme);
    }

    /// <summary>Открыть ссылку в браузере или приложении (WhatsApp, Telegram, телефон).</summary>
    public static bool OpenUrl(string? url)
    {
        try
        {
            if (!IsLaunchableUrl(url))
            {
                PosLogger.Log($"Ссылка не открыта: некорректный адрес «{url}».", "WARNING");
                return false;
            }
            url = url!.Trim();
            if (OperatingSystem.IsAndroid())
                return AndroidOpenUrl?.Invoke(url) ?? false;
            return StartDefault(url);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Ссылка не открылась ({ex.Message}).", "WARNING");
            return false;
        }
    }

    /// <summary>Открыть файл или папку в программе по умолчанию.</summary>
    public static bool OpenFile(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            if (OperatingSystem.IsAndroid())
            {
                if (!File.Exists(path))
                    return false;
                return AndroidOpenFile?.Invoke(path) ?? false;
            }
            return StartDefault(path);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Файл не открылся ({ex.Message}).", "WARNING");
            return false;
        }
    }

    /// <summary>Показать файл в проводнике (Windows, Finder) или открыть папку с ним (Linux). На Android папки «Файлы» для
    /// чужих программ нет — открываем сам файл (если есть программа для него).</summary>
    public static bool Reveal(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
                return true;
            }
            if (OperatingSystem.IsMacOS())
            {
                var psi = new ProcessStartInfo("open") { UseShellExecute = false };
                psi.ArgumentList.Add("-R");
                psi.ArgumentList.Add(path);
                Process.Start(psi)?.Dispose();
                return true;
            }
            if (OperatingSystem.IsLinux())
                return StartDefault(Path.GetDirectoryName(path) is { Length: > 0 } dir ? dir : ".");
            if (OperatingSystem.IsAndroid())
                return false;
            return false;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Папка не открылась ({ex.Message}).", "WARNING");
            return false;
        }
    }

    private static bool StartDefault(string target)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        var psi = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
        psi.ArgumentList.Add(target);
        Process.Start(psi)?.Dispose();
        return true;
    }
}
