namespace NurMarketKassa.Ui.Shared;

/// <summary>Выбор файла изображения для настроек кастомизации (Avalonia StorageProvider).</summary>
public interface ISettingsImagePicker
{
    Task<string?> PickBackgroundImageAsync();
}
