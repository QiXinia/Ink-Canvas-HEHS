using System.Text.Json;
using System.Text.RegularExpressions;
using InkCanvas.Extensibility;

namespace InkCanvas.Extensions;

public enum ExtensionState { Installed, Disabled, Starting, Running, Stopped, Failed, Blocked, NotInstalled }
public sealed record ExtensionStatus(string Id, string Name, string Version, ExtensionState State, string Error = null);

public static partial class ExtensionPackages
{
    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
    public static ExtensionManifest Read(string manifestPath)
    {
        if (new FileInfo(manifestPath).Length > 64 * 1024) throw new InvalidDataException("Extension manifest is too large.");
        var manifest = JsonSerializer.Deserialize<ExtensionManifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Extension manifest is empty.");
        Validate(manifest); return manifest;
    }
    public static void Validate(ExtensionManifest manifest)
    {
        if (manifest is null || manifest.Id is null || !Identifier().IsMatch(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name)
            || manifest.Name.Length > 100 || !Version.TryParse(manifest.Version, out _)) throw new InvalidDataException("Invalid extension identity or version.");
        if (manifest.ApiVersion != ExtensionApi.Version) throw new InvalidDataException($"Extension requires API {manifest.ApiVersion}, host supports {ExtensionApi.Version}.");
        if (manifest.Dependencies is null || manifest.Dependencies.Any(id => id is null || !Identifier().IsMatch(id))
            || manifest.Capabilities is null || manifest.Capabilities.Any(capability => !Enum.IsDefined(capability))) throw new InvalidDataException("Invalid extension dependency or capability.");
        if (manifest.ImportExtensions is null || manifest.ImportExtensions.Length > 16
            || (manifest.ImportExtensions.Length > 0) != manifest.Capabilities.Contains(PluginCapability.FileImport)
            || manifest.ImportExtensions.Any(ext => ext is null || !Regex.IsMatch(ext, @"\A\.[a-z0-9]{1,12}\z")
                || ext is ".icink" or ".pdf"))
            throw new InvalidDataException("Invalid file importer extensions.");
        if (string.IsNullOrWhiteSpace(manifest.EntryType) || manifest.EntryType.Length > 300) throw new InvalidDataException("Invalid extension entry type.");
    }
    public static string EntryPath(string directory, string entry)
    {
        directory = Path.GetFullPath(directory);
        if (string.IsNullOrWhiteSpace(entry) || Path.IsPathRooted(entry) || !entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Extension entry must be a relative DLL path.");
        string path = Path.GetFullPath(Path.Combine(directory, entry));
        if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Extension entry escapes its package.");
        for (var item = new FileInfo(path) as FileSystemInfo; item is not null && !item.FullName.Equals(directory, StringComparison.OrdinalIgnoreCase); item = Directory.GetParent(item.FullName))
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Extension entry cannot use a reparse point.");
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Extension directory cannot be a reparse point.");
        return path;
    }
}

public sealed class ExtensionPreferences
{
    public HashSet<string> DisabledModules { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> EnabledPlugins { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ExtensionPreferencesStore
{
    private readonly string _path;
    private readonly object _gate = new();
    public ExtensionPreferencesStore(string directory) => _path = Path.Combine(directory, "Extensions.json");
    public ExtensionPreferences Load()
    {
        lock (_gate)
        {
            try
            {
                var value = JsonSerializer.Deserialize<ExtensionPreferences>(File.ReadAllText(_path));
                return new ExtensionPreferences { DisabledModules = value?.DisabledModules ?? new(), EnabledPlugins = value?.EnabledPlugins ?? new() };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
        }
    }
    public void Save(ExtensionPreferences preferences)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temporary, JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, _path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
