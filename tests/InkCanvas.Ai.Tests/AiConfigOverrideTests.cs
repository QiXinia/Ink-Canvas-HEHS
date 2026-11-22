using System;
using InkCanvas.Platform.Ai;
using Xunit;

namespace InkCanvas.Ai.Tests;

/// <summary>
/// 串行化所有会读写 AI 环境变量/静态覆盖值的测试，避免并行污染。
/// </summary>
[CollectionDefinition("AiConfigEnvironment", DisableParallelization = true)]
public sealed class AiConfigEnvironmentCollection
{
}

[Collection("AiConfigEnvironment")]
public sealed class AiConfigOverrideTests : IDisposable
{
    private readonly string _previousEndpoint;
    private readonly string _previousModel;
    private readonly string _previousTimeout;
    private readonly string _previousImageTimeout;

    public AiConfigOverrideTests()
    {
        _previousEndpoint = Environment.GetEnvironmentVariable("AI_ENDPOINT");
        _previousModel = Environment.GetEnvironmentVariable("AI_MODEL");
        _previousTimeout = Environment.GetEnvironmentVariable("AI_TIMEOUT_MS");
        _previousImageTimeout = Environment.GetEnvironmentVariable("AI_IMAGE_TIMEOUT_MS");

        Environment.SetEnvironmentVariable("AI_ENDPOINT", null);
        Environment.SetEnvironmentVariable("AI_MODEL", null);
        Environment.SetEnvironmentVariable("AI_TIMEOUT_MS", null);
        Environment.SetEnvironmentVariable("AI_IMAGE_TIMEOUT_MS", null);
        AiConfig.ResetOverrides();
    }

    public void Dispose()
    {
        AiConfig.ResetOverrides();
        Environment.SetEnvironmentVariable("AI_ENDPOINT", _previousEndpoint);
        Environment.SetEnvironmentVariable("AI_MODEL", _previousModel);
        Environment.SetEnvironmentVariable("AI_TIMEOUT_MS", _previousTimeout);
        Environment.SetEnvironmentVariable("AI_IMAGE_TIMEOUT_MS", _previousImageTimeout);
    }

    [Fact]
    public void ApplyOverrides_WinsOverEnvironmentAndDefault()
    {
        Environment.SetEnvironmentVariable("AI_ENDPOINT", "https://env.example/v1/chat");
        Environment.SetEnvironmentVariable("AI_MODEL", "env-model");
        Environment.SetEnvironmentVariable("AI_TIMEOUT_MS", "9000");
        Environment.SetEnvironmentVariable("AI_IMAGE_TIMEOUT_MS", "9000");

        AiConfig.ApplyOverrides("https://flag.example/v1/chat", "flag-model", "12000", "13000");
        var config = AiConfig.ReadFromEnvironment();

        Assert.Equal("https://flag.example/v1/chat", config.Endpoint);
        Assert.Equal("flag-model", config.Model);
        Assert.Equal(12000, config.TimeoutMs);
        Assert.Equal(13000, config.ImageTimeoutMs);
    }

    [Fact]
    public void EmptyOverride_FallsBackToEnvironmentThenDefault()
    {
        Environment.SetEnvironmentVariable("AI_ENDPOINT", "https://env.example/v1/chat");
        Environment.SetEnvironmentVariable("AI_MODEL", "env-model");

        AiConfig.ApplyOverrides("", "   ");
        var fromEnvironment = AiConfig.ReadFromEnvironment();
        Assert.Equal("https://env.example/v1/chat", fromEnvironment.Endpoint);
        Assert.Equal("env-model", fromEnvironment.Model);
        Assert.Equal(AiConfig.DefaultTimeoutMs, fromEnvironment.TimeoutMs);
        Assert.Equal(AiConfig.DefaultImageTimeoutMs, fromEnvironment.ImageTimeoutMs);

        AiConfig.ResetOverrides();
        Environment.SetEnvironmentVariable("AI_ENDPOINT", null);
        Environment.SetEnvironmentVariable("AI_MODEL", null);
        var defaults = AiConfig.ReadFromEnvironment();
        Assert.Equal("", defaults.Endpoint);
        Assert.Equal(AiConfig.DefaultModel, defaults.Model);
    }

    [Fact]
    public void InvalidOverride_FallsBackToEnvironmentAndClampsValidValues()
    {
        Environment.SetEnvironmentVariable("AI_TIMEOUT_MS", "8000");
        Environment.SetEnvironmentVariable("AI_IMAGE_TIMEOUT_MS", "7000");

        AiConfig.ApplyOverrides(null, null, "not-a-number", "0");
        var fallback = AiConfig.ReadFromEnvironment();
        Assert.Equal(8000, fallback.TimeoutMs);
        Assert.Equal(7000, fallback.ImageTimeoutMs);

        AiConfig.ApplyOverrides(null, null, "999999", "50");
        var clamped = AiConfig.ReadFromEnvironment();
        Assert.Equal(AiConfig.MaxTimeoutMs, clamped.TimeoutMs);
        Assert.Equal(AiConfig.MinTimeoutMs, clamped.ImageTimeoutMs);
    }
}