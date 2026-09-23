using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>Хранит пользовательский шаблон этикетки на диске (один шаблон на машину/кассу).</summary>
public static class LabelTemplateStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NurMarketKassa", "label-template.json");

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
            PosLogger.Log($"Label template load failed: {ex.GetType().Name}", "WARNING");
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
            PosLogger.Log($"Label template save failed: {ex.GetType().Name}", "ERROR");
        }
    }
}
