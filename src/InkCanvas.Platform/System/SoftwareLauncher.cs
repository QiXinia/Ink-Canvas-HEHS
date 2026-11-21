using Microsoft.Win32;
using System.Diagnostics;

namespace InkCanvas.Platform.System;

/// <summary>
/// WPF-compatible launcher for the EasiCamera shortcut exposed by the original blackboard.
/// Searches both registry views because the vendor installer has shipped 32-bit and 64-bit
/// uninstall entries. Does not start or kill any process except on explicit user invocation.
/// </summary>
public static class SoftwareLauncher
{
    private const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public static bool LaunchEasiCamera(string softwareName = "希沃视频展台")
    {
        string exe = FindEasiCameraExecutablePath(softwareName);
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    /// <summary>Follows the upstream display-name → install/uninstall path convention.</summary>
    public static string FindEasiCameraExecutablePath(string softwareName)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var uninstall = root.OpenSubKey(UninstallPath);
                if (uninstall is null) continue;
                foreach (string name in uninstall.GetSubKeyNames())
                {
                    using var item = uninstall.OpenSubKey(name);
                    if (item?.GetValue("DisplayName") is not string display
                        || !display.Contains(softwareName, StringComparison.OrdinalIgnoreCase)) continue;

                    string install = item.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrWhiteSpace(install))
                    {
                        string candidate = Path.Combine(install, "sweclauncher.exe");
                        if (File.Exists(candidate)) return candidate;
                    }

                    string uninstallString = item.GetValue("UninstallString") as string;
                    if (string.IsNullOrWhiteSpace(uninstallString)) continue;
                    int slash = uninstallString.LastIndexOf('\\');
                    if (slash < 0) continue;
                    string folder = uninstallString[..slash];
                    string fallback = Path.Combine(folder, "sweclauncher", "sweclauncher.exe");
                    if (File.Exists(fallback)) return fallback;
                }
            }
            catch { /* registry view may be absent or ACL-restricted */ }
        }
        return null;
    }

    public static void LaunchDesmos()
    {
        Process.Start(new ProcessStartInfo("https://www.desmos.com/calculator?lang=zh-CN")
        {
            UseShellExecute = true,
        });
    }
}