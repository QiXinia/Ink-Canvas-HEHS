# Ink Canvas WinUI 3 作品说明

本文随 v3.14.0 发布，面向作品评审、教师安装使用和学校部署。Ink Canvas 是屏幕批注与课堂白板应用，本项目基于 Ink Canvas Artistry 迁移至 WinUI 3，提供画笔、荧光笔、面积擦、图形、选择、分页白板和 PPT 联动。PDF、解析几何、AI 助教、文稿库、云同步等能力通过模块扩展。

## 一、环境依赖与一键安装

### 使用环境

| 项目 | 要求与用途 |
| --- | --- |
| 操作系统 | 推荐 Windows 11 x64。项目最低 API 目标为 Windows 10 1809，系统和驱动的实际兼容性还需在目标设备验收 |
| .NET | .NET 10 x64 Runtime。安装相应的 x64 SDK 或 Desktop Runtime 也会包含所需的基础 Runtime |
| Windows App SDK | 默认 MSI、ZIP 已随程序携带所需组件 |
| 图形与输入 | 使用 DirectX／Win2D 渲染，支持鼠标、触摸和压感笔。高刷、多屏与红外触摸体验取决于设备、驱动及系统设置 |
| PPT／WPS 联动 | 安装对应的 Windows 桌面版软件，并在设置中启用相应支持 |
| 网络 | 下载更新、插件、调用远程 AI 和云同步时需要联网。已安装的本地批注与白板功能可离线使用 |

默认发布包的 `InkCanvas.runtimeconfig.json` 使用 `Microsoft.NETCore.App 10.0.0`。部署前应安装 .NET 10 x64 Runtime，安装时的管理员权限由 Windows 处理。[Microsoft .NET 安装说明](https://learn.microsoft.com/zh-cn/dotnet/core/install/windows)

### 一键安装脚本

仓库提供 `tools/install-runtime.ps1`。MSI 与 ZIP 包中还附有 `Tools/install-runtime.ps1`，Release 页面提供脚本单独下载。

脚本检查 Windows 版本、64 位环境及系统安装的 .NET 10 x64 Runtime。缺少运行时且 WinGet 可用时，从 `winget` 源安装 `Microsoft.DotNet.Runtime.10`，随后重新检查环境。已有运行时会直接复用。可传入 MSI 路径，在环境就绪后继续为当前用户安装 Ink Canvas。

在仓库根目录打开 PowerShell，执行环境检查或安装。

```powershell
# 仅检查，缺少依赖时返回错误
.\tools\install-runtime.ps1 -CheckOnly

# 检查并补装运行时
.\tools\install-runtime.ps1

# 补装运行时后继续安装下载好的 MSI
.\tools\install-runtime.ps1 -MsiPath .\InkCanvas-WinUI3-3.14.0-win-x64.msi
```

从发布 ZIP 中执行时，将上述 `tools` 路径替换为 `Tools`。从 Release 单独下载脚本时，直接运行 `./install-runtime.ps1`。安装系统运行时可能触发 UAC，应用 MSI 采用当前用户安装。

离线教室可预先下载 Microsoft 官方 .NET 10 x64 Runtime 安装程序与 Ink Canvas MSI，并提供本地路径。脚本校验运行时安装程序的 Microsoft 数字签名，执行后再次检查所需运行时。

```powershell
.\tools\install-runtime.ps1 `
    -RuntimeInstallerPath .\dotnet-runtime-10.0.0-win-x64.exe `
    -MsiPath .\InkCanvas-WinUI3-3.14.0-win-x64.msi
```

示例运行时文件名中的版本号可替换为实际下载的 .NET 10 稳定补丁版。无 WinGet 且未提供离线安装程序时，脚本给出官方获取地址和错误原因。PowerShell 的脚本执行限制按学校现有策略管理。[.NET 10 官方下载](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0)

### 源码构建环境

| 组件 | 当前项目使用方式 |
| --- | --- |
| .NET SDK 10 | 构建 `net10.0` 与 `net10.0-windows10.0.26100.0` 项目 |
| Windows SDK 10.0.26100.0 | 提供 Windows API、XAML 资源生成及原生模块构建所需工具 |
| Visual Studio Build Tools 2022 | 安装 MSBuild 与 C++ 桌面构建工具，完整打包资源管理器预览扩展时使用 |
| WiX Toolset 5.0.2 | 制作 MSI 安装包，并安装同版本 UI、Netfx 扩展 |
| NuGet 网络访问 | 首次还原项目依赖，版本由项目文件及 `Directory.Build.props` 指定 |

运行时安装脚本用于准备应用使用环境。源码构建按下节安装 SDK、Windows SDK 与构建工具。

## 二、部署与运行步骤

### 从 GitHub Release 安装

1. 下载同一正式版的 MSI 或 ZIP，以及 `SHA256SUMS.txt`。
2. 使用 `Get-FileHash` 检查安装包的 SHA256 与清单中同名文件对应值一致。
3. 执行上述环境脚本，再双击 MSI，或使用脚本的 `-MsiPath` 参数继续安装。
4. MSI 安装后从开始菜单启动。ZIP 解压到完整、可写的文件夹，运行其中的 `InkCanvas.exe`。
5. 首次打开设置，按课堂设备配置主题、显示器、单指／多指模式、PPT 支持以及白板存档目录。
6. 在“设置 → 模块与插件”下载、启用或更新扩展。PDF 阅读续接选项由 PDF 插件注册。

```powershell
Get-FileHash .\InkCanvas-WinUI3-3.14.0-win-x64.msi -Algorithm SHA256
```

右键 PDF 或 `.icink` 白板文件，在“打开方式”中选择 Ink Canvas。便携版可通过以下命令登记当前路径的文件打开方式。

```powershell
.\InkCanvas.exe --register-file-associations
.\InkCanvas.exe "D:\Lessons\example.pdf"
```

程序内更新会校验大小与 SHA256。安装版使用 MSI 升级，便携版使用 ZIP 更新。发布附件包含独立模块包，可通过程序内插件管理使用。安装与更新细节见配套说明。[安装说明](INSTALLATION.md) · [更新说明](UPDATES.md) · [模块与插件](EXTENSIONS.md)

### 教室批量部署

先由管理员部署 .NET 10 x64 Runtime，再以各目标教师的用户身份安装应用。MSI 默认为当前用户安装，系统账户下执行的结果需要与学校登录方式配套验证。

```powershell
msiexec.exe /i "InkCanvas-WinUI3-3.14.0-win-x64.msi" /qn /norestart
```

需要自定义安装位置时可传入 `INSTALLFOLDER="目标目录"`。部署后分别验证写字、擦除、白板翻页、PDF 打开、多屏定位与 PPT 切页。日志位于 `%APPDATA%\InkCanvas\Log.txt`，主配置位于 `%APPDATA%\InkCanvas\Settings.json`，文稿保存位置可在设置中调整。

### 从源码构建并运行

```powershell
git clone https://github.com/QiXinia/Ink-Canvas-HEHS.git
Set-Location Ink-Canvas-HEHS
git checkout v3.14.0

# 可改为任意具有写入权限的包缓存目录
.\build.ps1 build -Configuration Release -PackageCache "$env:LOCALAPPDATA\InkCanvas\NuGet"
.\build.ps1 test -Configuration Release -PackageCache "$env:LOCALAPPDATA\InkCanvas\NuGet"
.\build.ps1 run -Configuration Release -PackageCache "$env:LOCALAPPDATA\InkCanvas\NuGet"
```

只构建主程序时可使用以下命令。项目平台为 x64。

```powershell
dotnet publish .\src\InkCanvas.App\InkCanvas.App.csproj `
    -c Release -p:Platform=x64 -m:1 -p:BuildInParallel=false `
    -o .\artifacts\app
```

完整发行打包前，准备 Windows SDK、C++ 构建工具与 WiX。`tools/build-shell-extension.ps1` 会查找本机构建工具，WiX 使用以下固定版本。

```powershell
$wixTools = Join-Path $env:LOCALAPPDATA 'InkCanvas\BuildTools\wix'
dotnet tool install wix --tool-path $wixTools --version 5.0.2
& (Join-Path $wixTools 'wix.exe') extension add -g WixToolset.UI.wixext/5.0.2
& (Join-Path $wixTools 'wix.exe') extension add -g WixToolset.Netfx.wixext/5.0.2
.\tools\package-winui-release.ps1
```

完整打包生成主程序 ZIP、MSI、独立模块／插件包与 SHA256 校验清单。`tools/prune-builds.ps1` 按主程序构建时间保留 `artifacts` 中最近三份可运行版本。

### 可选云同步服务

客户端安装与本地白板运行完成后，可按需配置云同步。服务端使用 FastAPI、数据库与私有对象存储，部署流程及环境变量模板随源码提供。准备 Docker Compose、HTTPS 反向代理和对象存储，按服务端文档设置仓库外的环境文件，再执行 `docker compose up -d --build`。在客户端云同步插件中填写自己部署的 HTTPS 地址并登录。[服务端部署说明](https://github.com/QiXinia/Ink-Canvas-HEHS/blob/v3.14.0/server/cloud-sync/README.md)

部署实例的域名、IP、账号、令牌及对象存储密钥单独交付，公开文档与发布包使用通用配置模板。

## 三、额外使用的开源组件

项目保留 Ink Canvas Artistry 与 Ink Canvas 的上游贡献及 GPL-3.0 许可声明。以下按当前源码中的直接依赖和随包素材列出用途，具体条款以仓库及 NuGet 包附带的许可证为准。[项目许可证](https://github.com/QiXinia/Ink-Canvas-HEHS/blob/v3.14.0/LICENSE) · [Ink Canvas Artistry 上游](https://github.com/InkCanvas/Ink-Canvas-Artistry) · [Ink Canvas 上游](https://github.com/WXRIW/Ink-Canvas)

| 组件 | 当前版本 | 用途 | 许可与来源 |
| --- | --- | --- | --- |
| WinUI 3／Windows App SDK | WinUI 1.8.260803003、Runtime 1.8.260921001 | 原生窗口、Fluent 控件、窗口材质与输入 | 开源源码及微软二进制许可分别以对应发行条款为准。[Windows App SDK](https://github.com/microsoft/WindowsAppSDK) |
| Win2D | 1.4.0 | 墨迹、几何与画布 GPU 渲染 | MIT。[Win2D](https://github.com/microsoft/Win2D) |
| Fluent UI System Icons | 仓库随附字体 | 工具栏及插件图标，默认 Regular 样式 | MIT。[图标项目](https://github.com/microsoft/fluentui-system-icons) |
| Markdig | 1.6.0 | Markdown 版本说明与富文本显示 | BSD-2-Clause。[Markdig](https://github.com/xoofx/markdig) |
| PhotoSauce.NativeCodecs.Libwebp | 1.6.0-preview1 | WebP 图片解码 | 包装层 MIT，libwebp 为 BSD 风格许可。[PhotoSauce](https://github.com/saucecontrol/PhotoSauce) |
| PDFsharp | 6.2.4 | PDF 文件写入与矢量墨迹保存 | MIT。[PDFsharp](https://github.com/empira/PDFsharp) |
| PdfPig | 0.1.16 | PDF 文本提取、搜索及结构读取 | Apache-2.0。[PdfPig](https://github.com/UglyToad/PdfPig) |
| Clipper2 | 2.0.0 | 墨迹轮廓裁剪与几何运算 | Boost Software License 1.0。[Clipper2](https://github.com/AngusJohnson/Clipper2) |
| AngouriMath | 2.8.0 | 解析几何符号表达式与代数计算 | MIT。[AngouriMath](https://github.com/asc-community/AngouriMath) |
| Jint | 4.18.0 | 几何脚本执行 | BSD-2-Clause。[Jint](https://github.com/sebastienros/jint) |
| ModelContextProtocol.Core | 2.4.0 | 可选 MCP 模块的协议通信 | Apache-2.0。[MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) |
| LobeHub 与 OpenCode 图标 | 仓库随附 SVG | AI 服务商识别图标 | 随素材附带的 MIT 许可与品牌来源说明。[供应商图标说明](https://github.com/QiXinia/Ink-Canvas-HEHS/blob/v3.14.0/src/InkCanvas.Modules.AiAssistant/Assets/Providers/README.md) |

解析几何的传递依赖还包括 ANTLR、Acornima、GenericTensor、HonkSharp、Numbers 等，许可文本随模块的 `Licenses` 目录分发。图片、Markdown 和图标相关许可位于主程序 `Assets/Licenses`，PDF 与几何模块的许可位于各自 `Licenses` 目录。

可选云同步服务使用 FastAPI、Uvicorn、SQLAlchemy、psycopg、pwdlib、PyJWT、pydantic-settings 与 oss2。版本范围由服务端依赖文件管理，使用范围与服务部署有关。[服务端依赖清单](https://github.com/QiXinia/Ink-Canvas-HEHS/blob/v3.14.0/server/cloud-sync/requirements.txt)

## 四、码道在项目开发中的应用总结

本节预留，待补充实际开发过程与使用案例。

待填写内容包括应用场景、具体开发任务、人工审核与测试方式、典型案例及效果评估。
