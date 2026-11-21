using InkCanvas.Extensibility;

namespace InkCanvas.PluginFixture;

public sealed class GoodPlugin : IInkCanvasPlugin
{
    public ValueTask InitializeAsync(IPluginContext context, CancellationToken token)
    {
        context.RegisterCommand(new PluginCommand("run", "Healthy command"), (canvas, cancellation) =>
        {
            File.WriteAllText(Path.Combine(context.DataDirectory, "invoked.txt"), context.PluginId);
            return ValueTask.FromResult(new PluginCommandResult { Notification = context.PluginId + ":" + Environment.ProcessId });
        });
        return ValueTask.CompletedTask;
    }
}
public sealed class FailingStartup : IInkCanvasPlugin
{
    public ValueTask InitializeAsync(IPluginContext context, CancellationToken token) => throw new InvalidOperationException("fixture startup failed");
}
public sealed class CrashingPlugin : IInkCanvasPlugin
{
    public ValueTask InitializeAsync(IPluginContext context, CancellationToken token)
    {
        context.RegisterCommand(new PluginCommand("run", "Exit worker"), (canvas, cancellation) =>
        { Environment.Exit(23); return ValueTask.FromResult(new PluginCommandResult()); });
        return ValueTask.CompletedTask;
    }
}
public sealed class HangingPlugin : IInkCanvasPlugin
{
    public ValueTask InitializeAsync(IPluginContext context, CancellationToken token)
    {
        context.RegisterCommand(new PluginCommand("run", "Ignore cancellation"), async (canvas, cancellation) =>
        { await Task.Delay(Timeout.Infinite); return new PluginCommandResult(); });
        return ValueTask.CompletedTask;
    }
}
public sealed class InvalidOutputPlugin : IInkCanvasPlugin
{
    public ValueTask InitializeAsync(IPluginContext context, CancellationToken token)
    {
        context.RegisterCommand(new PluginCommand("run", "Invalid output"), (canvas, cancellation) => ValueTask.FromResult(new PluginCommandResult
        { Strokes = new[] { new PluginStroke { Width = float.NaN, Points = new[] { new PluginPoint(1, 2) } } } }));
        return ValueTask.CompletedTask;
    }
}
public sealed class SlowStartup : IInkCanvasPlugin
{
    public async ValueTask InitializeAsync(IPluginContext context, CancellationToken token)
    {
        await Task.Delay(500);
        context.RegisterCommand(new PluginCommand("run", "Slow startup command"), (canvas, cancellation) => ValueTask.FromResult(new PluginCommandResult()));
    }
}
