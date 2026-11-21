namespace InkCanvas.Core.Settings;

/// <summary>可回退的功能开关种类。</summary>
public enum FeatureKind
{
    AiTeaching,
    ExerciseFlow,
    ExampleLibrary,
    ResourceBundle,
}

/// <summary>
/// 功能开关读取入口。设置缺失、节点为空或未知开关时一律返回 true，
/// 即保持旧行为，避免升级后既有功能被意外关闭。
/// </summary>
public static class FeatureGate
{
    /// <summary>查询指定功能是否开启。Core 内没有全局设置，因此不提供无参重载。</summary>
    public static bool IsEnabled(FeatureKind kind, AppSettings settings)
    {
        FeatureSettings features = settings is null ? null : settings.Features;
        if (features is null)
        {
            return true;
        }

        return kind switch
        {
            FeatureKind.AiTeaching => features.EnableAiTeaching,
            FeatureKind.ExerciseFlow => features.EnableExerciseFlow,
            FeatureKind.ExampleLibrary => features.EnableExampleLibrary,
            FeatureKind.ResourceBundle => features.EnableResourceBundle,
            _ => true,
        };
    }
}