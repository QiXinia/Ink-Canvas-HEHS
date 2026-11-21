using System.Text.Json.Serialization;

namespace InkCanvas.Extensibility;

public static class ExtensionApi { public const int Version = 1; }

public sealed record ExtensionManifest
{
    public string Id { get; init; }
    public string Name { get; init; }
    public string Version { get; init; }
    public int ApiVersion { get; init; } = ExtensionApi.Version;
    public string EntryAssembly { get; init; }
    public string EntryType { get; init; }
    public string[] Dependencies { get; init; } = Array.Empty<string>();
    public PluginCapability[] Capabilities { get; init; } = Array.Empty<PluginCapability>();
    public string[] ImportExtensions { get; init; } = Array.Empty<string>();
    public string[] PackageFiles { get; init; } = Array.Empty<string>();
}

public interface IApplicationModule
{
    void Start(IModuleContext context);
    void Stop();
}

public interface IModuleContext
{
    T GetService<T>() where T : class;
    void Export<T>(T service) where T : class;
    void RegisterCleanup(Action cleanup);
    Action Protect(Action callback);
}

[JsonConverter(typeof(JsonStringEnumConverter<PluginCapability>))]
public enum PluginCapability { Notifications, CanvasWrite, FileImport }

public interface IInkCanvasPlugin
{
    ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken);
}

/// <summary>Converts a user-selected file to the host-assigned native page path.</summary>
public interface IInkFileImporter
{
    Task ConvertAsync(string sourcePath, string destinationPath);
}

public interface IPluginContext
{
    string PluginId { get; }
    string DataDirectory { get; }
    void RegisterCommand(PluginCommand command, Func<PluginCommandContext, CancellationToken, ValueTask<PluginCommandResult>> handler);
}

public sealed record PluginCommand(string Id, string Title, string Description = null);
public sealed record ExtensionCommand(string PluginId, string CommandId, string Title, string Description);
public sealed record PluginCommandContext(float ViewportWidth, float ViewportHeight, string CanvasKind);
public sealed record PluginPoint(float X, float Y);
public sealed record PluginStroke
{
    public float Width { get; init; } = 2;
    public uint Argb { get; init; } = 0xff2563eb;
    public PluginPoint[] Points { get; init; } = Array.Empty<PluginPoint>();
}
public sealed record PluginCommandResult
{
    public string Notification { get; init; }
    public PluginStroke[] Strokes { get; init; } = Array.Empty<PluginStroke>();
}

public sealed record PluginRequest(string Method, string CommandId = null, PluginCommandContext Context = null)
{
    public string SourcePath { get; init; }
    public string DestinationPath { get; init; }
}
public sealed record PluginResponse
{
    public bool Success { get; init; }
    public string Error { get; init; }
    public PluginCommand[] Commands { get; init; } = Array.Empty<PluginCommand>();
    public PluginCommandResult Result { get; init; }
}
