using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 例题库接入 UI 的静态契约：UI 层依赖 WinUI，无法在纯逻辑测试中实例化窗口，
/// 因此直接对源码断言关键调用存在，防止入口、检索、删除与功能开关被后续改动丢失。
/// </summary>
public sealed class ExampleLibraryUiContractTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "InkCanvas.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadSource(string relativePath)
    {
        string path = Path.Combine(
            RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), "缺少文件：" + relativePath);
        return File.ReadAllText(path);
    }

    [Fact]
    public void ExampleLibraryWindow_UsesStoreFeatureGateAndTheme()
    {
        string source = ReadSource("src/InkCanvas.App/Views/ExampleLibraryWindow.cs");

        Assert.Contains("ExampleLibraryStore.Search", source);
        Assert.Contains("ExampleLibraryStore.Delete", source);
        Assert.Contains("EnableExampleLibrary", source);
        Assert.Contains("ThemeBrushes.Resolve", source);
        Assert.Contains("WindowChrome.ApplyTheme", source);
        Assert.Contains("例题库", source);
    }

    [Fact]
    public void ToolkitWindow_ExposesExampleLibraryEntry()
    {
        string source = ReadSource("src/InkCanvas.App/Views/ToolkitWindow.cs");

        Assert.Contains("例题库", source);
        Assert.Contains("_openExampleLibrary", source);
    }

    [Fact]
    public void ShellController_ExposesSingleInstanceOpenMethod()
    {
        string source = ReadSource("src/InkCanvas.App/Services/ShellController.cs");

        Assert.Contains("public void OpenExampleLibrary()", source);
        Assert.Contains("new ExampleLibraryWindow(", source);
        Assert.Contains("_exampleLibrary", source);
    }
}