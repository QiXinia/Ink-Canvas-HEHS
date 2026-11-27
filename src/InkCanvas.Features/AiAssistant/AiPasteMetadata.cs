using System.Text.Json;

namespace InkCanvas.Features.AiAssistant;

/// <summary>How an AI paste card was authored; decides which field drives re-rendering.</summary>
public enum AiPasteKind { Formula, Steps, Shape }

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
        Latex ??= "";
        Text ??= "";
        Lines ??= new();
        Annotations ??= new();
    }
}