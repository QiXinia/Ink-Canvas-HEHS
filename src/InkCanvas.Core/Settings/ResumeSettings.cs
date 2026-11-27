using System.Text.Json.Serialization;

namespace InkCanvas.Core.Settings;

public enum ResumeBehavior { Disabled, Automatic, Reminder }

public sealed class ResumeSettings
{
    [JsonPropertyName("whiteboard")] public ResumeBehavior Whiteboard { get; set; } = ResumeBehavior.Reminder;
    [JsonPropertyName("pdf")] public ResumeBehavior Pdf { get; set; } = ResumeBehavior.Reminder;
}
