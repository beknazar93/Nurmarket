using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-05, владелец: «кнопку "скопировать информацию об устройстве" — чтобы легче было адаптировать
/// под такие устройства». Текст для поддержки: версия и режим программы, настройки экрана кассы и сведения об
/// аппарате (на Android — модель, экран, частоты, память; задаёт AndroidDeviceInfo). Без логинов, токенов,
/// имён компьютера и пользователя.</summary>
public static class DeviceInfoReport
{
    /// <summary>Сведения платформы (Android). null — Windows/Linux: собираются здесь.</summary>
    public static Func<string>? PlatformDetails { get; set; }

    public static string Build(Visual? anchor)
    {
        var sb = new StringBuilder();
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
                      ?? typeof(DeviceInfoReport).Assembly.GetName().Version?.ToString() ?? "?";
        sb.AppendLine($"NurMarket {version} — {(AppMode.IsOwner ? "программа владельца" : "касса")}");
        sb.AppendLine($"Время: {DateTime.Now:dd.MM.yyyy HH:mm:ss} (UTC{DateTimeOffset.Now:zzz})");
        sb.AppendLine($"Язык интерфейса: {UserPreferences.Instance.Language}; культура: {CultureInfo.CurrentUICulture.Name}");

        Line(sb, () =>
        {
            var p = UserPreferences.Instance;
            return $"Касса: вид «{p.MainLayoutMode}», масштаб {p.UiScalePercent:0}%, компактный вид «{p.CompactLayoutMode}», " +
                   $"размер карточек {p.CatalogTileScalePercent:0}%";
        });
        Line(sb, () => $"Тип аппарата: {(DeviceForm.IsHandheld ? "телефон/планшет (каталог и чек — отдельные экраны)" : "компьютер/терминал")}");
        Line(sb, () =>
        {
            if (anchor is null || Avalonia.Controls.TopLevel.GetTopLevel(anchor) is not { } top)
                return null;
            return $"Окно программы: {top.Bounds.Width:0}×{top.Bounds.Height:0} точек, плотность ×{top.RenderScaling:0.##}";
        });

        sb.AppendLine();
        if (PlatformDetails is { } platform)
        {
            Line(sb, platform);
        }
        else
        {
            Line(sb, () => $"ОС: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
            Line(sb, () => $"Процессор: {Environment.ProcessorCount} потоков, процесс {RuntimeInformation.ProcessArchitecture}");
            Line(sb, () => $".NET: {RuntimeInformation.FrameworkDescription}");
            Line(sb, () =>
            {
                var gc = GC.GetGCMemoryInfo();
                return $"Память: всего {gc.TotalAvailableMemoryBytes / 1048576} МБ, программа {Environment.WorkingSet / 1048576} МБ";
            });
            Line(sb, () =>
            {
                if (anchor is null || Avalonia.Controls.TopLevel.GetTopLevel(anchor)?.Screens is not { } screens)
                    return null;
                return "Экраны: " + string.Join("; ", screens.All.Select(s =>
                    $"{s.Bounds.Width}×{s.Bounds.Height} px, ×{s.Scaling:0.##}{(s.IsPrimary ? " (основной)" : "")}"));
            });
        }

        return sb.ToString().TrimEnd();
    }

    private static void Line(StringBuilder sb, Func<string?> part)
    {
        try
        {
            if (part() is { Length: > 0 } text)
                sb.AppendLine(text);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(не получено: {ex.GetType().Name})");
        }
    }
}
