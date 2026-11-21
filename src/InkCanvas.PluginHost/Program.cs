using System.IO.Pipes;
using System.Reflection;
using System.Runtime.Loader;
using InkCanvas.Extensibility;
using InkCanvas.Extensions;
using System.Diagnostics;

static string Argument(string key)
{
    int index = Array.IndexOf(Environment.GetCommandLineArgs(), key);
    return index >= 0 && index + 1 < Environment.GetCommandLineArgs().Length ? Environment.GetCommandLineArgs()[index + 1] : throw new ArgumentException("Missing " + key);
}

IInkCanvasPlugin plugin = null;
try
{
    // Exit even if a plugin ignores cancellation after the desktop host terminates.
    using var parent = Process.GetProcessById(int.Parse(Argument("--parent"), System.Globalization.CultureInfo.InvariantCulture));
    var parentMonitor = new Thread(() => { try { parent.WaitForExit(); } catch { return; } Environment.Exit(0); }) { IsBackground = true };
    parentMonitor.Start();
    string directory = Path.GetFullPath(Argument("--package"));
    var manifest = ExtensionPackages.Read(Path.Combine(directory, "plugin.json"));
    string entry = ExtensionPackages.EntryPath(directory, manifest.EntryAssembly);
    await using var pipe = new NamedPipeClientStream(".", Argument("--pipe"), PipeDirection.InOut, PipeOptions.Asynchronous);
    await pipe.ConnectAsync(5000);
    var context = new PluginContext(manifest.Id, Path.GetFullPath(Argument("--data")));
    while (true)
    {
        var request = await PluginWire.ReadAsync<PluginRequest>(pipe, CancellationToken.None);
        PluginResponse response;
        bool stop = request.Method == "shutdown";
        try
        {
            switch (request.Method)
            {
                case "initialize" when plugin is null:
                    var assembly = new PluginLoadContext(entry).LoadFromAssemblyPath(entry);
                    plugin = Activator.CreateInstance(assembly.GetType(manifest.EntryType, true)!) as IInkCanvasPlugin
                        ?? throw new InvalidDataException("Entry type does not implement IInkCanvasPlugin.");
                    if (manifest.ImportExtensions.Length > 0 && plugin is not IInkFileImporter)
                        throw new InvalidDataException("File importer interface is missing.");
                    Directory.CreateDirectory(context.DataDirectory);
                    await plugin.InitializeAsync(context, CancellationToken.None);
                    context.Seal();
                    response = new PluginResponse { Success = true, Commands = context.Commands.ToArray() };
                    break;
                case "execute" when plugin is not null:
                    var result = await context.ExecuteAsync(request.CommandId, request.Context);
                    PluginWire.ValidateResult(result, manifest.Capabilities);
                    response = new PluginResponse { Success = true, Result = result };
                    break;
                case "import" when plugin is IInkFileImporter importer:
                    if (!manifest.ImportExtensions.Contains(Path.GetExtension(request.SourcePath), StringComparer.OrdinalIgnoreCase))
                        throw new InvalidDataException("Unsupported import extension.");
                    await importer.ConvertAsync(request.SourcePath, request.DestinationPath);
                    response = new PluginResponse { Success = true };
                    break;
                case "shutdown":
                    if (plugin is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
                    else (plugin as IDisposable)?.Dispose();
                    plugin = null;
                    response = new PluginResponse { Success = true };
                    break;
                default: throw new InvalidOperationException("Invalid plugin lifecycle request.");
            }
        }
        catch (Exception ex) { response = new PluginResponse { Error = ex.GetBaseException().Message }; }
        await PluginWire.WriteAsync(pipe, response, CancellationToken.None);
        if (stop || (!response.Success && request.Method != "import")) break;
    }
}
catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException or ArgumentException) { Environment.ExitCode = 1; }
finally
{
    try { if (plugin is IAsyncDisposable disposable) await disposable.DisposeAsync(); else (plugin as IDisposable)?.Dispose(); } catch { }
}

sealed class PluginContext(string id, string data) : IPluginContext
{
    private readonly Dictionary<string, (PluginCommand Command, Func<PluginCommandContext, CancellationToken, ValueTask<PluginCommandResult>> Handler)> _commands = new(StringComparer.Ordinal);
    private bool _sealed;
    public string PluginId => id;
    public string DataDirectory => data;
    public IEnumerable<PluginCommand> Commands => _commands.Values.Select(value => value.Command);
    public void Seal() => _sealed = true;
    public void RegisterCommand(PluginCommand command, Func<PluginCommandContext, CancellationToken, ValueTask<PluginCommandResult>> handler)
    {
        if (_sealed || _commands.Count >= 32 || command is null || handler is null) throw new InvalidOperationException("Invalid command registration.");
        _commands.Add(command.Id, (command, handler));
    }
    public ValueTask<PluginCommandResult> ExecuteAsync(string id, PluginCommandContext context) =>
        context is not null && _commands.TryGetValue(id, out var entry) ? entry.Handler(context, CancellationToken.None) : throw new InvalidOperationException("Unknown plugin command.");
}

sealed class PluginLoadContext(string entry) : AssemblyLoadContext(isCollectible: false)
{
    private readonly AssemblyDependencyResolver _resolver = new(entry);
    protected override Assembly Load(AssemblyName name)
    {
        if (name.Name == typeof(IInkCanvasPlugin).Assembly.GetName().Name) return typeof(IInkCanvasPlugin).Assembly;
        string path = _resolver.ResolveAssemblyToPath(name);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
    protected override nint LoadUnmanagedDll(string name)
    {
        string path = _resolver.ResolveUnmanagedDllToPath(name);
        return path is null ? 0 : LoadUnmanagedDllFromPath(path);
    }
}
