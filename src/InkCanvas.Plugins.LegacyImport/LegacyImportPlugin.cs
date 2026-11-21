using System.Diagnostics;
using System.IO;
using InkCanvas.Extensibility;

namespace InkCanvas.Plugins.LegacyImport;

// The generic plugin worker has no WindowsDesktop framework. Only the decoder apphost starts WPF.
public sealed class LegacyImportPlugin : IInkCanvasPlugin, IInkFileImporter
{
    public ValueTask InitializeAsync(IPluginContext context, CancellationToken token) => ValueTask.CompletedTask;

    public async Task ConvertAsync(string sourcePath, string destinationPath)
    {
        var start = new ProcessStartInfo(Path.ChangeExtension(typeof(LegacyImportPlugin).Assembly.Location, ".exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        start.ArgumentList.Add(sourcePath);
        start.ArgumentList.Add(destinationPath);
        using var child = Process.Start(start) ?? throw new IOException("无法启动旧版文件解码器。");
        var errors = child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync();
        string error = await errors;
        if (child.ExitCode != 0)
            throw new InvalidDataException(string.IsNullOrWhiteSpace(error) ? "旧版文件解码失败。" : error.Trim());
    }
}
