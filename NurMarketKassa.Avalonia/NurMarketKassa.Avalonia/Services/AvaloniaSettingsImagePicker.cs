using Avalonia.Controls;
using Avalonia.Platform.Storage;
using NurMarketKassa.Services;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>Выбор изображения обоев через Avalonia StorageProvider.</summary>
public sealed class AvaloniaSettingsImagePicker : ISettingsImagePicker
{
    private static readonly FilePickerFileType[] ImageTypes =
    [
        new(Tr.T("Изображения", "Сүрөттөр", "Images", "Görseller", "Rasmlar"))
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
            Title = Tr.T("Выберите изображение для фона", "Фон үчүн сүрөттү тандаңыз", "Select a background image", "Arka plan görselini seçin", "Fon uchun rasmni tanlang"), // 2026-11-05 (А4-06): 5 языков
            AllowMultiple = false,
            FileTypeFilter = ImageTypes,
        }).ConfigureAwait(true);

        var file = files.FirstOrDefault();
        if (file is null)
            return null;

        // 2026-11-05 (А4-06): на Android локального пути нет — фон копируется в папку данных (путь хранится в настройках).
        return await ImportFileBridge.PermanentPathAsync(file).ConfigureAwait(true);
    }

    public async Task<string?> PickProductPhotoAsync()
    {
        var owner = GetTopLevel();
        if (owner?.StorageProvider is not { } storage)
            return null;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Tr.T("Выберите фото товара", "Товардын сүрөтүн тандаңыз", "Select a product photo", "Ürün fotoğrafını seçin", "Mahsulot rasmini tanlang"),
            AllowMultiple = false,
            FileTypeFilter = ImageTypes,
        }).ConfigureAwait(true);

        // 2026-11-05 (А4-06): на Android — временная копия (фото уходит на сервер, потом не нужно).
        return await ImportFileBridge.TemporaryPathAsync(files.FirstOrDefault()).ConfigureAwait(true);
    }

    private static TopLevel? GetTopLevel()
    {
        if (global::Avalonia.Application.Current?.ApplicationLifetime
#if NURANDROID
            is global::NurMarketKassa.IClassicDesktopStyleApplicationLifetime desktop) // 2026-10-04: Android-касса
#else
            is global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
#endif
            return desktop.MainWindow;

        return null;
    }
}
