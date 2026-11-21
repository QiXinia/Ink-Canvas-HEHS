using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using InkCanvas.Extensibility;
using InkCanvas.Extensions;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class ExtensionRuntimeTests
{
    [Fact]
    public void PluginSdkAndInkCoreDoNotReferenceHostOrOptionalImplementations()
    {
        Assert.DoesNotContain(typeof(ExtensionApi).Assembly.GetReferencedAssemblies(), assembly => assembly.Name!.StartsWith("InkCanvas.") || assembly.Name.StartsWith("Microsoft.UI"));
        Assert.DoesNotContain(typeof(InkCanvas.Core.Ink.InkStroke).Assembly.GetReferencedAssemblies(), assembly => assembly.Name!.StartsWith("InkCanvas.Modules") || assembly.Name.StartsWith("InkCanvas.Extensions") || assembly.Name == "InkCanvas");
    }
    private static ExtensionManifest Manifest(string id, params string[] dependencies) => new()
    { Id = id, Name = id, Version = "1.0.0", EntryAssembly = "fixture.dll", EntryType = "Fixture.Module", Dependencies = dependencies };
    private interface ICounter { void Run(); }
    private sealed class Counter(Action action) : ICounter { public void Run() => action(); }
    private sealed class Module(Action<IModuleContext> start, Action stop = null) : IApplicationModule
    { public void Start(IModuleContext context) => start(context); public void Stop() => stop?.Invoke(); }

    [Fact]
    public void ModuleDependenciesStartInOrderAndStopInReverseOrder()
    {
        var order = new List<string>();
        var runtime = new ModuleRuntime();
        runtime.Register(Manifest("module.child", "module.base"), () => new Module(context => order.Add("child"), () => order.Add("stop-child")));
        runtime.Register(Manifest("module.base"), () => new Module(context => { order.Add("base"); context.RegisterCleanup(() => order.Add("cleanup")); }, () => order.Add("stop-base")));
        runtime.Start(new HashSet<string>());
        Assert.Equal(new[] { "base", "child" }, order); Assert.All(runtime.Status, status => Assert.Equal(ExtensionState.Running, status.State));
        runtime.Dispose(); runtime.Dispose(); Assert.Equal(new[] { "base", "child", "stop-child", "stop-base", "cleanup" }, order);
    }

    [Fact]
    public void FailingModuleRollsBackItsExportsAndCleanupWhileHealthyModuleContinues()
    {
        using var runtime = new ModuleRuntime(); bool cleaned = false; int healthy = 0;
        runtime.Register(Manifest("module.broken"), () => new Module(context =>
        { context.RegisterCleanup(() => cleaned = true); context.Export<object>(new object()); throw new InvalidOperationException("startup failure"); }));
        runtime.Register(Manifest("module.healthy"), () => new Module(context => context.Export<ICounter>(new Counter(() => healthy++))));
        runtime.Start(new HashSet<string>());
        Assert.True(cleaned); Assert.False(runtime.HasExport<object>()); Assert.True(runtime.Invoke<ICounter>(counter => counter.Run())); Assert.Equal(1, healthy);
        Assert.Equal(ExtensionState.Failed, runtime.Status.Single(status => status.Id == "module.broken").State);
    }

    [Fact]
    public void GuardedCallbackFailureStopsDependentsAndCannotFireAgain()
    {
        using var runtime = new ModuleRuntime(); Action callback = null; int executed = 0; bool dependentStopped = false;
        runtime.Register(Manifest("module.base"), () => new Module(context =>
        { callback = context.Protect(() => { executed++; throw new Exception("callback failed"); }); context.Export<ICounter>(new Counter(() => { })); }));
        runtime.Register(Manifest("module.child", "module.base"), () => new Module(context => { }, () => dependentStopped = true));
        runtime.Start(new HashSet<string>()); callback(); callback();
        Assert.Equal(1, executed); Assert.True(dependentStopped); Assert.False(runtime.HasExport<ICounter>());
        Assert.All(runtime.Status, status => Assert.Equal(ExtensionState.Failed, status.State));
    }

    [Fact]
    public void MissingDisabledAndCyclicDependenciesNeverStartTheirConsumers()
    {
        using var runtime = new ModuleRuntime(); int started = 0;
        runtime.Register(Manifest("module.disabled"), () => new Module(context => started++));
        runtime.Register(Manifest("module.child", "module.disabled"), () => new Module(context => started++));
        runtime.Register(Manifest("module.missing", "module.absent"), () => new Module(context => started++));
        runtime.Register(Manifest("module.cycle-a", "module.cycle-b"), () => new Module(context => started++));
        runtime.Register(Manifest("module.cycle-b", "module.cycle-a"), () => new Module(context => started++));
        runtime.Start(new HashSet<string> { "module.disabled" });
        Assert.Equal(0, started); Assert.DoesNotContain(runtime.Status, status => status.State == ExtensionState.Running);
        Assert.Contains(runtime.Status, status => status.Error?.Contains("cycle") == true);
    }

    [Fact]
    public void MissingModulePackageDoesNotPreventHealthyStartupAndRegistrationIsFrozen()
    {
        using var runtime = new ModuleRuntime(); int calls = 0;
        runtime.RegisterPackage("module.missing", "Missing module", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "module.json"));
        runtime.Register(Manifest("module.healthy"), () => new Module(context => context.Export<ICounter>(new Counter(() => calls++))));
        runtime.Start(new HashSet<string>()); runtime.Invoke<ICounter>(counter => counter.Run()); Assert.Equal(1, calls);
        Assert.Equal(ExtensionState.NotInstalled, runtime.Status.Single(status => status.Id == "module.missing").State);
        Assert.Throws<InvalidOperationException>(() => runtime.Register(Manifest("module.late"), () => new Module(context => { })));
    }

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("C:\\outside.dll")]
    [InlineData("not-a-library.exe")]
    public void PackageEntryCannotEscapeOrLoadAnExecutable(string entry)
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "inkcanvas-package-" + Guid.NewGuid().ToString("N"))).FullName;
        try { Assert.Throws<InvalidDataException>(() => ExtensionPackages.EntryPath(root, entry)); }
        finally { Directory.Delete(root); }
    }

    [Fact]
    public async Task ProtocolRejectsOversizedTruncatedAndInvalidFrames()
    {
        using var stream = new MemoryStream(); byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, PluginWire.MaximumMessageBytes + 1); stream.Write(header); stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(async () => await PluginWire.ReadAsync<PluginRequest>(stream, default));
        stream.SetLength(0); stream.Position = 0; BinaryPrimitives.WriteInt32LittleEndian(header, 100); stream.Write(header); stream.Position = 0;
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await PluginWire.ReadAsync<PluginRequest>(stream, default));
    }

    [Fact]
    public void GeometryAndCapabilitiesAreValidatedBeforeCanvasChanges()
    {
        var strokes = new[] { new PluginStroke { Points = new[] { new PluginPoint(1, 2) } } };
        Assert.Throws<InvalidDataException>(() => PluginWire.ValidateResult(new PluginCommandResult { Strokes = strokes }, Array.Empty<PluginCapability>()));
        Assert.Throws<InvalidDataException>(() => PluginWire.ValidateResult(new PluginCommandResult { Strokes = new[] { strokes[0] with { Width = float.NaN } } }, new[] { PluginCapability.CanvasWrite }));
        Assert.Throws<InvalidDataException>(() => PluginWire.ValidateResult(new PluginCommandResult { Strokes = new[] { strokes[0] with { Points = new[] { new PluginPoint(float.PositiveInfinity, 1) } } } }, new[] { PluginCapability.CanvasWrite }));
        PluginWire.ValidateResult(new PluginCommandResult { Strokes = strokes }, new[] { PluginCapability.CanvasWrite });
    }

    [Fact]
    public void PreferencesAreIndependentFromApplicationSettingsAndTolerateLegacyFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "inkcanvas-preferences-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var store = new ExtensionPreferencesStore(root); Assert.Empty(store.Load().EnabledPlugins);
            var preferences = new ExtensionPreferences(); preferences.EnabledPlugins.Add("sample.plugin"); preferences.DisabledModules.Add("module.optional"); store.Save(preferences);
            Assert.Contains("sample.plugin", store.Load().EnabledPlugins); Assert.Contains("module.optional", store.Load().DisabledModules);
            File.WriteAllText(Path.Combine(root, "Extensions.json"), "{broken"); Assert.Empty(store.Load().EnabledPlugins);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PluginCommandsRunInSeparateProcessesWithSeparateDataAndCanRestart()
    {
        using var fixture = new PluginsFixture(); fixture.Add("test.first", "GoodPlugin"); fixture.Add("test.second", "GoodPlugin");
        using var manager = fixture.Manager(); manager.Discover(fixture.Packages);
        await manager.StartEnabledAsync(new HashSet<string> { "test.first", "test.second" });
        Assert.All(manager.Status, status => Assert.Equal(ExtensionState.Running, status.State));
        var first = await manager.ExecuteAsync("test.first", "run", new(800, 600, "board"));
        var second = await manager.ExecuteAsync("test.second", "run", new(800, 600, "screen"));
        int firstPid = int.Parse(first.Notification.Split(':')[1]), secondPid = int.Parse(second.Notification.Split(':')[1]);
        Assert.NotEqual(Environment.ProcessId, firstPid); Assert.NotEqual(firstPid, secondPid);
        Assert.Equal("test.first", File.ReadAllText(Path.Combine(fixture.Data, "plugin-test.first", "invoked.txt")));
        await manager.SetEnabledAsync("test.first", false); Assert.DoesNotContain(manager.Commands, command => command.PluginId == "test.first");
        await manager.SetEnabledAsync("test.first", true); Assert.Equal(2, manager.Commands.Count);
    }

    [Theory]
    [InlineData("FailingStartup")]
    [InlineData("CrashingPlugin")]
    [InlineData("HangingPlugin")]
    [InlineData("InvalidOutputPlugin")]
    public async Task PluginFailuresCannotRemoveHealthyCommandsOrStopHealthyWorkers(string type)
    {
        using var fixture = new PluginsFixture(); fixture.Add("test.broken", type); fixture.Add("test.healthy", "GoodPlugin");
        using var manager = fixture.Manager(); manager.Discover(fixture.Packages);
        await manager.StartEnabledAsync(new HashSet<string> { "test.broken", "test.healthy" });
        if (type != "FailingStartup") await Assert.ThrowsAnyAsync<Exception>(() => manager.ExecuteAsync("test.broken", "run", new(800, 600, "board")));
        Assert.Equal(ExtensionState.Failed, manager.Status.Single(status => status.Id == "test.broken").State);
        Assert.DoesNotContain(manager.Commands, command => command.PluginId == "test.broken");
        Assert.StartsWith("test.healthy:", (await manager.ExecuteAsync("test.healthy", "run", new(800, 600, "board"))).Notification);
    }

    [Fact]
    public async Task LatestEnableIntentWinsWhilePluginIsStillStarting()
    {
        using var fixture = new PluginsFixture(); fixture.Add("test.slow", "SlowStartup"); using var manager = fixture.Manager(); manager.Discover(fixture.Packages);
        var start = manager.SetEnabledAsync("test.slow", true); await Task.Delay(100); var stop = manager.SetEnabledAsync("test.slow", false);
        await Task.WhenAll(start, stop); Assert.Empty(manager.Commands); Assert.Equal(ExtensionState.Disabled, manager.Status.Single().State);
    }

    [Fact]
    public async Task UnsupportedApiDuplicateIdsAndMissingWorkerAreReportedWithoutCrashingHost()
    {
        using var fixture = new PluginsFixture(); fixture.Add("test.api", "GoodPlugin", api: 999); fixture.Add("test.duplicate", "GoodPlugin"); fixture.Add("test.duplicate", "GoodPlugin", folder: "duplicate-copy");
        using var manager = fixture.Manager(); manager.Discover(fixture.Packages); await manager.StartEnabledAsync(new HashSet<string> { "test.api", "test.duplicate" });
        Assert.All(manager.Status, status => Assert.Equal(ExtensionState.Failed, status.State)); Assert.Empty(manager.Commands);
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.SetEnabledAsync("test.duplicate", true));
        fixture.Add("test.valid", "GoodPlugin");
        using var missingWorker = new PluginManager(Path.Combine(fixture.Root, "missing.exe"), fixture.Data);
        missingWorker.Discover(fixture.Packages); await missingWorker.SetEnabledAsync("test.valid", true); Assert.Equal(ExtensionState.Failed, missingWorker.Status.Single(status => status.Id == "test.valid").State);
    }

    private sealed class PluginsFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "inkcanvas-plugin-tests-" + Guid.NewGuid().ToString("N"));
        public string Packages => Path.Combine(Root, "Plugins");
        public string Data => Path.Combine(Root, "Data");
        private static string Repository
        {
            get
            {
                var directory = new DirectoryInfo(AppContext.BaseDirectory);
                while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InkCanvas.slnx"))) directory = directory.Parent;
                return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
            }
        }
        public PluginsFixture() => Directory.CreateDirectory(Packages);
        public void Add(string id, string type, int api = 1, string folder = null)
        {
#if DEBUG
            const string configuration = "Debug";
#else
            const string configuration = "Release";
#endif
            string source = Path.Combine(Repository, "tests", "InkCanvas.PluginFixture", "bin", "x64", configuration, "net10.0", "win-x64");
            string target = Path.Combine(Packages, folder ?? id); Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            File.WriteAllText(Path.Combine(target, "plugin.json"), JsonSerializer.Serialize(new ExtensionManifest
            { Id = id, Name = id, Version = "1.0.0", ApiVersion = api, EntryAssembly = "InkCanvas.PluginFixture.dll", EntryType = "InkCanvas.PluginFixture." + type, Capabilities = new[] { PluginCapability.Notifications, PluginCapability.CanvasWrite } }));
        }
        public PluginManager Manager()
        {
#if DEBUG
            const string configuration = "Debug";
#else
            const string configuration = "Release";
#endif
            return new PluginManager(Path.Combine(Repository, "src", "InkCanvas.PluginHost", "bin", "x64", configuration, "net10.0", "win-x64", "InkCanvas.PluginHost.exe"), Data, TimeSpan.FromSeconds(3));
        }
        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch (IOException) { }
        }
    }
}
