using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>Хранит пользовательский шаблон ценника на диске (один шаблон на машину/кассу),
/// отдельно от шаблона этикетки (<see cref="LabelTemplateStore"/>) — те же типы данных
/// (<see cref="LabelTemplate"/>), но разные файлы, чтобы кастомизация ценника и этикетки не
/// путались друг с другом (2026-09-06, "Пользовательский шаблон" рядом с 6 готовыми пресетами
/// ценника).</summary>
public static class PriceTagTemplateStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        NurMarketKassa.Services.AppMode.DataFolderName, "price-tag-template.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static LabelTemplate Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var template = JsonSerializer.Deserialize<LabelTemplate>(File.ReadAllText(FilePath), JsonOptions);
                if (template is not null)
                    return template;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Price tag template load failed: {ex.GetType().Name}", "WARNING");
        }
        return LabelTemplate.CreateDefault();
    }

    public static void Save(LabelTemplate template)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(template, JsonOptions));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Price tag template save failed: {ex.GetType().Name}", "ERROR");
        }
    }
}
