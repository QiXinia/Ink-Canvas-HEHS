using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// AI 讲题与随堂练习开关接入 UI 的静态契约：AssistantWindow 依赖 WinUI，
/// 无法在纯逻辑测试中实例化，因此直接对源码断言入口与状态文案存在，
/// 并确保开关判断集中在 UpdateActionState 内，避免散落到多个方法。
/// </summary>
public sealed class FeatureGateUiContractTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "InkCanvas.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string AssistantWindowSource()
    {
        string path = Path.Combine(
            RepoRoot(), "src", "InkCanvas.App", "Views", "AssistantWindow.cs");
        Assert.True(File.Exists(path), "缺少文件：src/InkCanvas.App/Views/AssistantWindow.cs");
        return File.ReadAllText(path);
    }

    private static int Count(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string UpdateActionStateBody(string source)
    {
        int start = source.IndexOf("private void UpdateActionState()", StringComparison.Ordinal);
        Assert.True(start >= 0, "未找到 UpdateActionState");
        int end = source.IndexOf("private static bool IsActive", start, StringComparison.Ordinal);
        Assert.True(end > start, "未找到 UpdateActionState 结束边界");
        return source[start..end];
    }

    [Fact]
    public void AssistantWindow_ContainsFeatureSwitchesAndDisabledStatusText()
    {
        string source = AssistantWindowSource();

        Assert.Contains("EnableAiTeaching", source);
        Assert.Contains("EnableExerciseFlow", source);
        Assert.Contains("AI 讲题已关闭", source);
        Assert.Contains("随堂练习已关闭", source);
    }

    [Fact]
    public void UpdateActionState_ReadsBothFeatureSwitchesOnce()
    {
        string body = UpdateActionStateBody(AssistantWindowSource());

        int aiTeaching = Count(body, "EnableAiTeaching");
        int exerciseFlow = Count(body, "EnableExerciseFlow");

        Assert.Equal(1, aiTeaching);
        Assert.Equal(1, exerciseFlow);
        Assert.Equal(2, aiTeaching + exerciseFlow);
    }

    [Fact]
    public void UpdateActionState_GatesSendFollowUpAndVariantButtons()
    {
        string body = UpdateActionStateBody(AssistantWindowSource());

        Assert.Contains("_send.IsEnabled = !_busy && aiTeachingEnabled;", body);
        Assert.Contains("_followUp.IsEnabled = !_busy && aiTeachingEnabled;", body);
        Assert.Contains("_variantBasic.IsEnabled = exerciseFlowEnabled;", body);
        Assert.Contains("_variantAdvanced.IsEnabled = exerciseFlowEnabled;", body);
    }
}