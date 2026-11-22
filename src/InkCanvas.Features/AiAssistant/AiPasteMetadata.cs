using System.Text.Json;

namespace InkCanvas.Features.AiAssistant;

/// <summary>How an AI paste card was authored; decides which field drives re-rendering.</summary>
public enum AiPasteKind { Formula, Steps, Shape, BoardPlan }

/// <summary>
/// Editable source stored alongside an AI paste PNG so the card can be re-opened, edited and
/// re-rendered. Persisted as camelCase JSON inside <c>BoardImageAsset.Metadata</c>.
/// </summary>
public sealed class AiPasteMetadata
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        IncludeFields = true,
    };

    public int Version = CurrentVersion;
    public AiPasteKind Kind;
    public string Latex = "";
    public string Text = "";
    public List<string> Lines = new();
    public List<List<double>> Annotations = new();

    /// <summary>BoardPlan 块类型（text/formula/wordlist/poem）；旧卡片为空，按 text 处理。</summary>
    public string BlockKind = "";

    /// <summary>BoardPlan 块标题（古诗/段落）；旧卡片为空。</summary>
    public string Title = "";

    /// <summary>BoardPlan 古诗作者或朝代；旧卡片为空。</summary>
    public string Author = "";

    /// <summary>board_plan schema 版本；旧卡片没有该字段时按 1 处理。</summary>
    public int Schema = 1;

    /// <summary>AI 板书样式（字号/对齐/列/行距）的 camelCase JSON；旧卡片为空。</summary>
    public string StyleJson = "";

    /// <summary>生成该卡片时的自然语言指令；旧卡片为空。</summary>
    public string Instruction = "";

    /// <summary>生成时间（Unix 毫秒）；旧卡片为 0，表示未记录。</summary>
    public long CreatedAt;

    /// <summary>计划级与块级提示；旧卡片为空列表。</summary>
    public List<string> Notes = new();

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Parse stored metadata; returns <c>null</c> for empty, malformed or foreign JSON.</summary>
    public static AiPasteMetadata TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var metadata = JsonSerializer.Deserialize<AiPasteMetadata>(json, JsonOptions);
            if (metadata is null) return null;
            metadata.Normalize();
            return metadata;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private void Normalize()
    {
        if (Version <= 0) Version = CurrentVersion;
        if (Schema <= 0) Schema = 1;
        Latex ??= "";
        Text ??= "";
        Lines ??= new();
        Annotations ??= new();
        StyleJson ??= "";
        Instruction ??= "";
        Notes ??= new();
        BlockKind ??= "";
        Title ??= "";
        Author ??= "";
    }
}