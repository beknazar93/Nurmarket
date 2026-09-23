using Avalonia.Controls;
using Avalonia.Platform.Storage;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>Выбор изображения обоев через Avalonia StorageProvider.</summary>
public sealed class AvaloniaSettingsImagePicker : ISettingsImagePicker
{
    private static readonly FilePickerFileType[] ImageTypes =
    [
        new("Изображения")
        {
            Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"],
            MimeTypes = ["image/png", "image/jpeg", "image/webp"],
        },
    ];

    public async Task<string?> PickBackgroundImageAsync()
    {
        var owner = GetTopLevel();
        if (owner?.StorageProvider is not { } storage)
            return null;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите изображение для фона",
            AllowMultiple = false,
            FileTypeFilter = ImageTypes,
        }).ConfigureAwait(true);

        var file = files.FirstOrDefault();
        if (file is null)
            return null;

        return file.TryGetLocalPath();
    }

    private static TopLevel? GetTopLevel()
    {
        if (global::Avalonia.Application.Current?.ApplicationLifetime
            is global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;

        return null;
    }
}
