# InkCanvas.Extensibility

InkCanvas 的 API v1 契约包，面向 .NET 10 和当前 x64 宿主。该包不引用 WinUI、主程序或墨迹引擎实现。

外部插件实现 `IInkCanvasPlugin`，在 `InitializeAsync` 中通过 `IPluginContext.RegisterCommand` 登记命令。宿主为每个插件启动独立工作进程，并通过命名管道交换声明式数据。

```csharp
public sealed class ExamplePlugin : IInkCanvasPlugin
{
    public ValueTask InitializeAsync(IPluginContext context, CancellationToken token)
    {
        context.RegisterCommand(new PluginCommand("hello", "示例命令"),
            (canvas, cancellation) => ValueTask.FromResult(new PluginCommandResult
            {
                Notification = "插件已运行",
            }));
        return ValueTask.CompletedTask;
    }
}
```

项目启用 `EnableDynamicLoading`，包目录包含 `plugin.json`、入口 DLL、`.deps.json` 和私有依赖。清单指定 `ApiVersion` 为 1，并声明 `Notifications` 或 `CanvasWrite` 能力。引用 SDK 时设置 `Private="false"`，工作进程提供共享的契约程序集。

API v1 支持命令、视口尺寸、画布类型、通知和可编辑笔画输出。结果由宿主验证后应用，画布变更记录为单次撤销。每个插件最多登记 32 个命令，每次结果最多包含 256 条笔画和 16384 个点。

插件自己的数据目录由 `IPluginContext.DataDirectory` 提供。初始化及命令需要及时完成，超时、退出或非法结果会停用该工作进程。进程隔离用于故障和依赖隔离，插件仍以当前用户权限运行。

仓库中的 `samples/InkCanvas.SamplePlugin` 提供完整示例，`docs/EXTENSIONS.md` 说明目录、生命周期、分包及宿主接口。
