using System.Diagnostics;
using System.Text.Json;
using InkCanvas.Core.Update;

if (args.Length != 1) return 2;
string planPath = Path.GetFullPath(args[0]), directory = Path.GetDirectoryName(planPath);
UpdatePlan plan = null;
bool parentExited = false;
try
{
    plan = JsonSerializer.Deserialize<UpdatePlan>(await File.ReadAllTextAsync(planPath)) ?? throw new InvalidDataException("Missing update plan.");
    if (plan.ParentProcessId > 0)
    {
        try
        {
            using var parent = Process.GetProcessById(plan.ParentProcessId);
            if (parent.StartTime.ToUniversalTime().Ticks == plan.ParentStartedUtcTicks
                && string.Equals(parent.MainModule?.FileName, plan.ParentExecutable, StringComparison.OrdinalIgnoreCase))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                await parent.WaitForExitAsync(timeout.Token);
            }
        }
        catch (ArgumentException) { }
    }
    parentExited = true;
    // The launcher retains this lease while alive. Hold it through replacement and rollback
    // so another Ink Canvas instance cannot reclaim the updater's working directory.
    using var cacheLease = new FileStream(Path.Combine(Path.GetDirectoryName(directory), ".lease"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    // File locks held by runtime teardown can outlive the parent process briefly.
    for (int attempt = 0; ; attempt++)
    {
        try
        {
            await UpdatePackage.ApplyAsync(plan, directory);
            break;
        }
        catch (IOException) when (attempt < 4) { await Task.Delay(1000); }
    }
    await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new UpdateResult(true, plan.Version, "更新完成")));
    InkCanvas.Core.Diagnostics.FileLog.WriteLogToFile("Update installed " + plan.Version);
    if (plan.Restart) Process.Start(new ProcessStartInfo(Path.Combine(plan.TargetDirectory, "InkCanvas.exe")) { WorkingDirectory = plan.TargetDirectory, UseShellExecute = true });
    return 0;
}
catch (Exception exception)
{
    InkCanvas.Core.Diagnostics.FileLog.WriteLogToFile("Update failed: " + exception, InkCanvas.Core.Diagnostics.LogType.Error);
    await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new UpdateResult(false, plan?.Version, exception.ToString())));
    if (parentExited && plan?.Restart == true && !File.Exists(Path.Combine(directory, "journal.json"))
        && File.Exists(Path.Combine(plan.TargetDirectory, "InkCanvas.exe")))
        Process.Start(new ProcessStartInfo(Path.Combine(plan.TargetDirectory, "InkCanvas.exe")) { WorkingDirectory = plan.TargetDirectory, UseShellExecute = true });
    return 1;
}
