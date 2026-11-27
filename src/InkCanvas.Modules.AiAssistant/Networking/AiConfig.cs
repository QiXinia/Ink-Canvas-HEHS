// @parity harmony/platform/src/main/ets/assistant/AiConfig.ets
using System.Globalization;
using InkCanvas.Core.AiAssistant;

namespace InkCanvas.Platform.Ai;

/// <summary>
/// AI 助教运行时配置。端点为空时走本地 mock 流，保证无网络也能自测；
/// 真实服务端点通过 <c>AI_ENDPOINT</c> 注入，无需改代码。
/// </summary>
public sealed class AiConfig
{
    public const string DefaultModel = "inkcanvas-assistant";
    public const int DefaultTimeoutMs = 15000;
    public const int DefaultImageTimeoutMs = 20000;
    public const int MinTimeoutMs = 1000;
    public const int MaxTimeoutMs = 120000;

    public string Endpoint { get; set; } = "";

    public string Model { get; set; } = DefaultModel;

    /// <summary>纯墨迹阶段的超时。</summary>
    public int TimeoutMs { get; set; } = DefaultTimeoutMs;

    /// <summary>带截图精修阶段的超时，默认 20 秒。</summary>
    public int ImageTimeoutMs { get; set; } = DefaultImageTimeoutMs;

    /// <summary>
    /// 从 <c>AI_ENDPOINT</c> / <c>AI_MODEL</c> / <c>AI_TIMEOUT_MS</c> / <c>AI_IMAGE_TIMEOUT_MS</c> 读取配置。
    /// 空模型回退默认模型；非法或非正的超时回退默认值，合法值钳制在 1000–120000 ms。
    /// </summary>
    public static AiConfig ReadFromEnvironment()
    {
        return new AiConfig
        {
            Endpoint = ReadString("AI_ENDPOINT", ""),
            Model = ReadString("AI_MODEL", DefaultModel),
            TimeoutMs = ParseTimeoutMs(ReadString("AI_TIMEOUT_MS", ""), DefaultTimeoutMs),
            ImageTimeoutMs = ParseTimeoutMs(ReadString("AI_IMAGE_TIMEOUT_MS", ""), DefaultImageTimeoutMs),
        };
    }

    /// <summary>按阶段选择首个 token 超时。</summary>
    public int TimeoutForStage(AssistantStage stage)
    {
        int value = stage == AssistantStage.InkOnly ? TimeoutMs : ImageTimeoutMs;
        return value > 0 ? value : (stage == AssistantStage.InkOnly ? DefaultTimeoutMs : DefaultImageTimeoutMs);
    }

    /// <summary>单行摘要，供设置界面与诊断日志使用；端点为空时显示 mock 流。</summary>
    public string Describe()
    {
        string endpoint = string.IsNullOrEmpty(Endpoint) ? "mock（本地流式）" : Endpoint;
        return "AI 服务：" + endpoint + " · 模型 " + Model
            + " · 无图超时 " + TimeoutMs + "ms"
            + " · 带图超时 " + ImageTimeoutMs + "ms";
    }

    private static string ReadString(string name, string fallback)
    {
        string value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? fallback : value;
    }

    private static int ParseTimeoutMs(string raw, int fallback)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed <= 0)
        {
            return fallback;
        }
        return Math.Clamp(parsed, MinTimeoutMs, MaxTimeoutMs);
    }
}