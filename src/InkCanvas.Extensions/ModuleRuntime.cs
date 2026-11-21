using System.Reflection;
using System.Runtime.Loader;
using InkCanvas.Extensibility;

namespace InkCanvas.Extensions;

/// <summary>Owns trusted module lifecycle, dependency order, exports and rollback.</summary>
public sealed class ModuleRuntime : IDisposable
{
    private sealed class Module
    {
        public ExtensionManifest Manifest;
        public Func<IApplicationModule> Factory;
        public IApplicationModule Instance;
        public Context Context;
        public ExtensionState State;
        public string Error;
    }
    private readonly Dictionary<string, Module> _modules = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, object> _services = new();
    private readonly Dictionary<Type, (string Owner, object Service)> _exports = new();
    private readonly List<Module> _started = new();
    private bool _disposed, _starting, _hasStarted;
    public IReadOnlyList<ExtensionStatus> Status => _modules.Values.Select(m => new ExtensionStatus(m.Manifest.Id, m.Manifest.Name, m.Manifest.Version, m.State, m.Error)).ToArray();
    public event Action Changed;
    public void AddService<T>(T service) where T : class => _services.Add(typeof(T), service);
    public void Register(ExtensionManifest manifest, Func<IApplicationModule> factory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ExtensionPackages.Validate(manifest);
        if (_starting || _hasStarted) throw new InvalidOperationException("Register modules before startup.");
        if (!_modules.TryAdd(manifest.Id, new Module { Manifest = manifest, Factory = factory, State = ExtensionState.Installed })) throw new InvalidDataException("Duplicate module ID.");
    }
    public void RegisterPackage(string id, string name, string manifestPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hasStarted) throw new InvalidOperationException("Register modules before startup.");
        if (!File.Exists(manifestPath))
        {
            var missing = new ExtensionManifest { Id = id, Name = name, Version = "0.0.0", EntryType = "NotInstalled" };
            ExtensionPackages.Validate(missing);
            if (!_modules.TryAdd(id, new Module { Manifest = missing, State = ExtensionState.NotInstalled })) throw new InvalidDataException("Duplicate module ID.");
            return;
        }
        try
        {
            var manifest = ExtensionPackages.Read(manifestPath);
            if (manifest.Id != id) throw new InvalidDataException("Module package identity does not match its host registration.");
            string entry = ExtensionPackages.EntryPath(Path.GetDirectoryName(manifestPath)!, manifest.EntryAssembly);
            Register(manifest, () =>
            {
                var context = new ModuleLoadContext(entry);
                var type = context.LoadFromAssemblyPath(entry).GetType(manifest.EntryType, true)!;
                return Activator.CreateInstance(type) as IApplicationModule ?? throw new InvalidDataException("Module does not implement IApplicationModule.");
            });
        }
        catch (Exception ex)
        {
            _modules.Add(id, new Module { Manifest = new ExtensionManifest { Id = id, Name = name, Version = "0.0.0" }, State = ExtensionState.Failed, Error = ex.Message });
        }
    }
    public void Start(IReadOnlySet<string> disabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hasStarted) throw new InvalidOperationException("Module startup can only run once.");
        _hasStarted = true; _starting = true;
        foreach (var module in _modules.Values.Where(module => disabled.Contains(module.Manifest.Id))) module.State = ExtensionState.Disabled;
        foreach (var module in _modules.Values) Start(module, new HashSet<string>(StringComparer.Ordinal));
        _starting = false; Notify();
    }
    private bool Start(Module module, HashSet<string> visiting)
    {
        if (module.State == ExtensionState.Running) return true;
        if (module.State is ExtensionState.Failed or ExtensionState.Disabled or ExtensionState.Blocked or ExtensionState.Stopped or ExtensionState.NotInstalled) return false;
        if (!visiting.Add(module.Manifest.Id)) { module.State = ExtensionState.Failed; module.Error = "Module dependency cycle."; return false; }
        foreach (string id in module.Manifest.Dependencies)
        {
            if (!_modules.TryGetValue(id, out var dependency) || !Start(dependency, visiting))
            {
                if (module.State != ExtensionState.Failed) { module.State = ExtensionState.Blocked; module.Error = $"Required module unavailable: {id}"; }
                visiting.Remove(module.Manifest.Id); return false;
            }
        }
        visiting.Remove(module.Manifest.Id);
        module.State = ExtensionState.Starting; module.Context = new Context(this, module);
        try
        {
            module.Instance = module.Factory(); module.Instance.Start(module.Context);
            if (module.State != ExtensionState.Starting) return false;
            module.State = ExtensionState.Running; _started.Add(module); return true;
        }
        catch (Exception ex) { Fail(module, ex); return false; }
    }
    public bool Invoke<T>(Action<T> callback) where T : class
    {
        if (_disposed || !_exports.TryGetValue(typeof(T), out var value)) return false;
        var module = _modules[value.Owner];
        try { callback((T)value.Service); return true; }
        catch (Exception ex) { Fail(module, ex); return false; }
    }
    public bool HasExport<T>() where T : class => !_disposed && _exports.ContainsKey(typeof(T));
    public T GetExport<T>() where T : class => !_disposed && _exports.TryGetValue(typeof(T), out var value) ? (T)value.Service : null;
    private void Fail(Module module, Exception error)
    {
        if (module.State is ExtensionState.Failed or ExtensionState.Stopped) return;
        foreach (var dependent in _started.AsEnumerable().Reverse().Where(item => item.Manifest.Dependencies.Contains(module.Manifest.Id)).ToArray())
            Fail(dependent, new InvalidOperationException($"Required module failed: {module.Manifest.Id}"));
        Stop(module); module.State = ExtensionState.Failed; module.Error = error.GetBaseException().Message; Notify();
    }
    private void Stop(Module module)
    {
        foreach (var type in _exports.Where(item => item.Value.Owner == module.Manifest.Id).Select(item => item.Key).ToArray()) _exports.Remove(type);
        try { module.Instance?.Stop(); } catch (Exception ex) { module.Error = ex.Message; }
        module.Context?.Dispose(); module.Instance = null; module.Context = null;
        if (module.State != ExtensionState.Disabled) module.State = ExtensionState.Stopped;
    }
    private void Notify()
    {
        foreach (Action observer in Changed?.GetInvocationList() ?? Array.Empty<Delegate>()) try { observer(); } catch { }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var module in _started.AsEnumerable().Reverse()) Stop(module);
        _exports.Clear(); _services.Clear(); _started.Clear();
    }
    private sealed class Context(ModuleRuntime runtime, Module owner) : IModuleContext, IDisposable
    {
        private readonly Stack<Action> _cleanup = new();
        private bool _disposed;
        public T GetService<T>() where T : class => runtime._services.TryGetValue(typeof(T), out var value) ? (T)value : throw new InvalidOperationException($"Host service unavailable: {typeof(T).Name}");
        public void Export<T>(T service) where T : class
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            runtime._exports.Add(typeof(T), (owner.Manifest.Id, service ?? throw new ArgumentNullException(nameof(service))));
        }
        public void RegisterCleanup(Action cleanup) { if (_disposed) cleanup(); else _cleanup.Push(cleanup); }
        public Action Protect(Action callback) => () =>
        {
            if (owner.State is not (ExtensionState.Running or ExtensionState.Starting)) return;
            try { callback(); } catch (Exception ex) { runtime.Fail(owner, ex); }
        };
        public void Dispose() { _disposed = true; while (_cleanup.TryPop(out var cleanup)) try { cleanup(); } catch { } }
    }
    private sealed class ModuleLoadContext(string entry) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(entry);
        private static readonly HashSet<string> Shared = new(StringComparer.Ordinal)
        {
            "InkCanvas.Extensibility", "InkCanvas.Host.Contracts", "InkCanvas.Core", "InkCanvas.Platform",
            "InkCanvas.Ink", "InkCanvas.Features", "InkCanvas.Controls", "Microsoft.WinUI",
            "Microsoft.InteractiveExperiences.Projection", "Microsoft.Windows.SDK.NET", "WinRT.Runtime", "Microsoft.Graphics.Canvas", "Microsoft.Graphics.Canvas.Interop",
        };
        protected override Assembly Load(AssemblyName name)
        {
            if (Shared.Contains(name.Name)) return Default.LoadFromAssemblyName(name);
            string path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
        protected override nint LoadUnmanagedDll(string name)
        {
            string path = _resolver.ResolveUnmanagedDllToPath(name);
            return path is null ? 0 : LoadUnmanagedDllFromPath(path);
        }
    }
}
