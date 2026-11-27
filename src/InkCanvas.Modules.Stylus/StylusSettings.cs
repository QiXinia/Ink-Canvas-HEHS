using System.Text.Json;
using System.Text.Json.Serialization;

namespace InkCanvas.Modules.Stylus;

public enum StylusAction { None, Pen, Highlighter, AreaEraser, StrokeEraser, Select, Shape, Pan, Undo, Redo, Larger, Smaller }
public enum StylusSwitchMode { Temporary, Permanent, Toggle }
public sealed record StylusBinding(string Input, string Name, StylusAction Action, StylusSwitchMode Mode);

public sealed class StylusSettings
{
    public bool Enabled { get; set; } = true;
    public List<StylusBinding> Bindings { get; set; } = Defaults();
    public static List<StylusBinding> Defaults() => new()
    {
        new("pen:barrel", "手写笔主侧键", StylusAction.AreaEraser, StylusSwitchMode.Temporary),
        new("pen:secondary", "手写笔第二侧键", StylusAction.Select, StylusSwitchMode.Temporary),
        new("pen:eraser", "倒置笔尾 / 橡皮端", StylusAction.AreaEraser, StylusSwitchMode.Temporary),
    };
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static StylusSettings Load(string directory)
    {
        try
        {
            string path = Path.Combine(directory, "buttons.json");
            var settings = File.Exists(path) ? JsonSerializer.Deserialize<StylusSettings>(File.ReadAllText(path), Json) : new();
            settings ??= new();
            settings.Bindings = (settings.Bindings ?? Defaults()).Where(binding => binding is not null
                && !string.IsNullOrWhiteSpace(binding.Input) && Enum.IsDefined(binding.Action) && Enum.IsDefined(binding.Mode))
                .DistinctBy(binding => binding.Input).Take(64).ToList();
            foreach (var binding in Defaults())
                if (!settings.Bindings.Any(item => item.Input == binding.Input)) settings.Bindings.Insert(0, binding);
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Core.Diagnostics.FileLog.WriteLogToFile("手写笔配置读取失败 " + ex.Message);
            return new();
        }
    }

    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "buttons.json");
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
        File.Move(temporary, path, overwrite: true);
    }
}
