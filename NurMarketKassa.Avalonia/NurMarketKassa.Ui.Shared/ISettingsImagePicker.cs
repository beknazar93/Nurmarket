namespace NurMarketKassa.Ui.Shared;

/// <summary>Выбор файла изображения для настроек кастомизации (Avalonia StorageProvider).</summary>
public interface ISettingsImagePicker
{
    Task<string?> PickBackgroundImageAsync();

    /// <summary>Выбор локального файла фото товара для загрузки на сервер.</summary>
    Task<string?> PickProductPhotoAsync();
}
