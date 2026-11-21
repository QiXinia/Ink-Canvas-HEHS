using InkCanvas.Extensibility;

namespace InkCanvas.SamplePlugin;

public sealed class GuideLinesPlugin : IInkCanvasPlugin
{
    public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.RegisterCommand(new PluginCommand("guide-lines", "插入参考线", "在当前画布中插入两条参考线，支持一次撤销"), (canvas, token) =>
        {
            float left = canvas.ViewportWidth * .15f, right = canvas.ViewportWidth * .85f;
            float top = canvas.ViewportHeight * .35f, bottom = canvas.ViewportHeight * .65f;
            return ValueTask.FromResult(new PluginCommandResult
            {
                Strokes = new[]
                {
                    new PluginStroke { Points = new[] { new PluginPoint(left, top), new PluginPoint(right, top) } },
                    new PluginStroke { Points = new[] { new PluginPoint(left, bottom), new PluginPoint(right, bottom) } },
                },
            });
        });
        return ValueTask.CompletedTask;
    }
}
