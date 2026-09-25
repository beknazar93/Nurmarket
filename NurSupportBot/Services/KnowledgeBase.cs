using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using NurSupportBot.Data;

namespace NurSupportBot.Services;

/// <summary>Элемент файла базы знаний (seed/kb.json, выгрузка /export): раздел с children или инструкция.</summary>
public sealed class KbItem
{
    public string Title { get; set; } = "";
    public string? Text { get; set; }
    public List<string>? Keywords { get; set; }
    public string? MediaKind { get; set; }
    public string? MediaFileId { get; set; }
    public string? VideoUrl { get; set; }
    public bool? Archived { get; set; }
    public List<KbItem>? Children { get; set; }
}

/// <summary>Загрузка базы знаний из JSON и выгрузка обратно. Нужна для начального наполнения и
/// для массовой правки: выгрузил /export, поправил файл, загрузил /import.</summary>
public static class KnowledgeBase
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static int Import(Db db, string json, bool replace)
    {
        var items = JsonSerializer.Deserialize<List<KbItem>>(json, Json) ?? throw new InvalidDataException("Пустой файл.");
        if (replace)
        {
            foreach (var root in db.Children(null, includeArchived: true))
                db.DeleteNode(root.Id);
        }
        var count = 0;
        void Add(long? parent, List<KbItem> list)
        {
            foreach (var item in list)
            {
                if (string.IsNullOrWhiteSpace(item.Title))
                    continue;
                var isArticle = item.Children is not { Count: > 0 };
                var id = db.AddNode(parent, item.Title.Trim(), isArticle, item.Text?.Trim(),
                    item.Keywords is { Count: > 0 } k ? string.Join(", ", k) : null);
                if (!string.IsNullOrWhiteSpace(item.MediaFileId))
                {
                    db.UpdateNode(id, "media_kind", item.MediaKind ?? "video");
                    db.UpdateNode(id, "media_file_id", item.MediaFileId);
                }
                if (!string.IsNullOrWhiteSpace(item.VideoUrl))
                    db.UpdateNode(id, "video_url", item.VideoUrl);
                if (item.Archived == true)
                    db.UpdateNode(id, "archived", 1);
                count++;
                if (!isArticle)
                    Add(id, item.Children!);
            }
        }
        Add(null, items);
        return count;
    }

    public static string Export(Db db)
    {
        List<KbItem> Build(long? parent) => db.Children(parent, includeArchived: true).Select(n => new KbItem
        {
            Title = n.Title,
            Text = n.IsArticle ? n.Text : null,
            Keywords = string.IsNullOrWhiteSpace(n.Keywords)
                ? null
                : n.Keywords.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList(),
            MediaKind = n.MediaFileId != null ? n.MediaKind : null,
            MediaFileId = n.MediaFileId,
            VideoUrl = n.VideoUrl,
            Archived = n.Archived ? true : null,
            Children = n.IsArticle ? null : Build(n.Id),
        }).ToList();
        return JsonSerializer.Serialize(Build(null), Json);
    }
}
