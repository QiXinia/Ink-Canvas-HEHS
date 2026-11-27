# 构建环境（最小化）

本分支已从 WPF / .NET Framework 4.7.2 迁移到 **WinUI 3（Windows App SDK）**。
使用环境与一键安装脚本见 [作品说明](docs/PROJECT-DESCRIPTION.md)，MSI 与便携版安装见 [安装说明](docs/INSTALLATION.md)。
下面记录实测可用的最小工具链，以及刻意**不装**的东西。

## 必需

| 组件 | 版本（实测） | 说明 |
|---|---|---|
| .NET SDK | 10.0.404 | 目标框架 `net10.0-windows10.0.26100.0` |
| Windows SDK | 10.0.26100.0 | 提供 `makepri` / `signtool` 等 |
| VS Build Tools 2022 | 17.14（含 MSBuild + VC 14.44） | 只需 MSBuild，不需要 IDE |

NuGet 包（首次还原自动下载）：`Microsoft.WindowsAppSDK.WinUI` 1.8.260803003、
`Microsoft.WindowsAppSDK.Runtime` 1.8.260921001、`Microsoft.Graphics.Win2D` 1.4.0 及其依赖。

## 刻意不装

- **VS「通用 Windows 平台开发」工作负载**（数 GB）。应用采用**非打包**部署
  （`WindowsPackageType=None`），因此不需要 MSIX / `AppxPackage` MSBuild targets。
- **.NET 8 / 9 运行时**。直接目标 `net10.0`，实测可构建可运行。
- **Windows App SDK monolith 包**。改为引用子包，跳过 AI / ML / Widgets / Search，
  实测少拉约 200 MB。
- **Office PIA**。PPT 交互改用 COM `dynamic` 后期绑定。
- **Tablet PC InkAnalyzer（IACore/IALoader/IAWinFX）**。图形识别改为托管几何拟合，
  这同时解除了上游的 x86 锁定。

## 磁盘占用（实测）

NuGet 缓存约 **1.28 GB**（含 monolith 试装阶段的残留；仅子包时更小）。
缓存位置由 `build.ps1` 的 `-PackageCache` 指定，默认 `D:\dev-cache\nuget`，
**刻意避开系统盘**。

## 发布体积

| 方式 | 体积 | 前提 |
|---|---|---|
| 默认：Windows App SDK 自包含 + 裁剪 | 以当前发布附件为准 | 目标机需先装 .NET 10 x64 Runtime |
| `-FrameworkDependent` | 按所选模块与运行时载荷变化 | 目标机需先装 .NET 10 x64 Runtime 和匹配的 Windows App SDK 运行时 |
| 未裁剪的自包含（对照） | 约 140 MB | — |

```powershell
.\build.ps1 publish                      # 携带 Windows App SDK，仍需 .NET 10 x64 Runtime
.\build.ps1 publish -FrameworkDependent   # 另需匹配的 Windows App SDK 运行时
```

裁剪掉的是**本应用用不到的** Windows App SDK 自带载荷：Windows AI / ML
（`onnxruntime.dll` 20.7 MB + `DirectML.dll` 17.8 MB）、Widgets、Semantic Search，
以及多余语言资源与符号文件。裁剪后已实机启动验证。

剩下的体积主要是 WinUI 3 本身的固定成本，无法再显著压缩：
`Microsoft.Windows.SDK.NET.dll` 25 MB（WinRT 投影）、`Microsoft.ui.xaml.dll` 14 MB、
`Microsoft.WinUI.dll` 7 MB、`Microsoft.UI.Xaml.Controls.dll` 6 MB。
这是 WinUI 3 自包含部署与 WPF（依赖系统内置 .NET Framework）的结构性差异。

## 两个容易踩的坑

1. **机器级 NuGet 配置可能没有任何包源**，此时还原会直接 `NU1100` 失败。
   仓库根的 `NuGet.config` 已显式声明 nuget.org，任何克隆都能还原。
2. **平台必须显式指定 x64**。解决方案只声明了 x64；
   不带 `-p:Platform=x64` 会报 `MSB4126`。用 `build.ps1` 可避免。

## 命令

```powershell
.\build.ps1 build      # 全解决方案
.\build.ps1 test       # Core 单元测试（含笔锋回归）
.\build.ps1 run        # 构建并启动
.\build.ps1 selftest   # 运行时自检，输出 selftest.json
.\build.ps1 publish    # 输出到 artifacts\app
```

等价的原生命令：

```powershell
$env:NUGET_PACKAGES = 'D:\dev-cache\nuget'
dotnet build InkCanvas.slnx -c Debug -p:Platform=x64
dotnet test tests\InkCanvas.Core.Tests\InkCanvas.Core.Tests.csproj -c Debug -p:Platform=x64
```

## 运行时自检

`InkCanvas.exe --selftest=<目录>` 会走真实输入路径画几笔，然后写出 `selftest.json`，
其中 `widthsIdentical` / `outlineIdentical` 断言**提交后的笔画几何与抬笔前最后一帧完全一致**
—— 这正是笔锋闪烁的判据。`--board` 直接进画板。
