using System;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

// 2026-10-23, ТЗ «Маркетплейс — редизайн и медиа» (пакет П-0): основа для слайдов и анимаций на странице «подробно».
// Здесь — только описания и поиск файлов; сами файлы лежат в Assets/marketplace/<MediaFolder>/ (кладут пакеты медиа),
// рисует их страница «подробно» (MarketplaceStoreView.Detail.cs). Пока файлов нет — ResolveSlide/ResolveDemo возвращают null.

/// <summary>Группа элемента в витрине. Заполняется MarketplaceContent.Enrich.</summary>
public enum MarketplaceGroup { Sales, Warehouse, Owner, Packs, Layouts, Themes, Soon }

/// <summary>Слайд: имя файла без языка и расширения ("slide-1") + подпись уже на языке программы.</summary>
public sealed record MarketplaceSlide(string FileStem, string Caption);

/// <summary>Выгода: ключ векторного значка (MarketplaceIcons), заголовок, текст.</summary>
public sealed record MarketplaceBenefit(string IconKey, string Title, string Text);

/// <summary>Анимация «Как это работает»: имя GIF без языка ("demo") + подпись.</summary>
public sealed record MarketplaceDemo(string FileStem, string Caption);

/// <summary>Откуда берутся байты медиа по URI. Сейчас — только ресурсы программы (avares); сетевой источник с кэшем — отдельной задачей.</summary>
public interface IMarketplaceMediaSource
{
    /// <summary>Открыть файл по URI; null — файла нет. Поток закрывает вызывающий.</summary>
    Stream? Open(string uri);
}

/// <summary>Медиа из ресурсов программы (avares://NurMarketKassa.Avalonia/Assets/marketplace/…).</summary>
public sealed class LocalMarketplaceMediaSource : IMarketplaceMediaSource
{
    public Stream? Open(string uri)
    {
        try
        {
            var parsed = new Uri(uri);
            return AssetLoader.Exists(parsed) ? AssetLoader.Open(parsed) : null;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Маркетплейс: медиа «{uri}» не открылось: {ex.Message}", "WARNING");
            return null;
        }
    }
}

/// <summary>Поиск медиа элемента по правилам ТЗ 4.2: сначала «имя.язык.расширение», затем «имя.расширение»;
/// расширения слайда — .webp, затем .png; анимации — .gif. Язык — как в базе знаний (ru — без суффикса).</summary>
public static class MarketplaceMedia
{
    private const string Root = "avares://NurMarketKassa.Avalonia/Assets/marketplace/";
    private static readonly string[] SlideExtensions = [".webp", ".png"];
    private static readonly string[] DemoExtensions = [".gif"];

    /// <summary>Откуда читать файлы (замена — для будущего сетевого источника).</summary>
    public static IMarketplaceMediaSource Source { get; set; } = new LocalMarketplaceMediaSource();

    /// <summary>URI слайда на языке программы (иначе русского) или null, если файла нет.</summary>
    public static string? ResolveSlide(MarketplaceItem item, MarketplaceSlide slide) =>
        Resolve(item, slide.FileStem, SlideExtensions);

    /// <summary>URI анимации на языке программы (иначе русской) или null, если файла нет.</summary>
    public static string? ResolveDemo(MarketplaceItem item) =>
        item.Demo is { } demo ? Resolve(item, demo.FileStem, DemoExtensions) : null;

    /// <summary>Загрузить картинку по URI, найденному ResolveSlide; null — не открылась.</summary>
    public static Bitmap? LoadBitmap(string uri)
    {
        try
        {
            using var stream = Source.Open(uri);
            return stream is null ? null : new Bitmap(stream);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Маркетплейс: картинка «{uri}» не загрузилась: {ex.Message}", "WARNING");
            return null;
        }
    }

    private static string? Resolve(MarketplaceItem item, string stem, string[] extensions)
    {
        var language = Tr.T("ru", "ky", "en", "tr", "uz");
        var folder = Root + item.MediaFolder + "/";
        if (language != "ru")
            foreach (var ext in extensions)
            {
                var uri = folder + stem + "." + language + ext;
                if (Exists(uri))
                    return uri;
            }

        foreach (var ext in extensions)
        {
            var uri = folder + stem + ext;
            if (Exists(uri))
                return uri;
        }
        return null;
    }

    private static bool Exists(string uri)
    {
        try
        {
            return AssetLoader.Exists(new Uri(uri));
        }
        catch
        {
            return false;
        }
    }
}
