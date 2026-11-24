# Ink Canvas 作品说明

本文以 Windows v3.15.0 及当前源码为依据，面向作品评审、教师使用和学校部署。项目在 Ink Canvas 与 Ink Canvas Artistry 的开源基础上开展架构迁移、代码重构与功能扩展，重建桌面界面、墨迹渲染、输入交互和模块接口，持续改进 UI、UX 与课堂使用流程。鸿蒙分支采用 ArkTS／ArkUI 实现原生适配，具体完成范围和验证状态单独说明。

## 项目重构与设计改进

项目保留上游来源、署名与 GPL-3.0 许可。本项目的开发工作覆盖现代桌面技术迁移、系统输入与渲染、数据持久化、原生界面重设计和扩展能力，具体改造可以通过当前代码、界面截图与验证记录核对。

| 维度 | 本项目实施内容 | 对课堂使用的影响 |
| --- | --- | --- |
| 技术迁移 | 从 WPF／.NET Framework 桌面实现演进到 C#、.NET 10、WinUI 3、Win2D 与 Windows App SDK | 接入原生 Fluent 控件、窗口材质及现代输入能力 |
| 架构与模块 | 划分 Core、Platform、Ink、Controls、Features、App，建立 Host.Contracts、Extensibility 和扩展运行时 | 主体书写与 PDF、数学、AI、文稿库等能力分包维护 |
| 墨迹管线 | 可变笔宽模型、因果笔锋、硬件压力与倾斜、历史分块缓存、活动墨迹脏区更新 | 支持多种笔刷，减少快速连续书写、平移与捏合中的重复计算 |
| 窗口与交互 | 重做设置、PDF、解析几何、AI 侧栏、文稿库和课堂工具，统一标题栏、Regular 图标、主题资源及响应式布局 | 适配深浅色、触摸热区、可折叠导航与可拖离选项面板 |
| 数据与对象 | 异步 ZIP 流式存档、独立媒体条目、逐页纸型、保留形状构造、PDF 矢量批注与自动页边出血 | 板书与对象保存后可以继续编辑和复用 |
| 桌面适配 | 多屏坐标与 DPI、无焦点激活、PPT 放映／阅读联动、实验性滚动缩放跟随 | 在原有课件与桌面界面中开展批注，按设备能力调整交互 |

架构与实现说明见 [模块与插件](EXTENSIONS.md)、[迁移对照](PORTING-NOTES.md) 和 [性能记录](PERFORMANCE.md)。性能记录中的单机离屏测量附有场景与范围，实体红外屏、压感笔和混合 DPI 双屏的端到端体验仍需在目标设备验收。

## 功能与课堂流程

- 现场书写提供画笔、荧光笔、八种笔迹风格、自定义色板、真实压力及笔速笔锋，配合滑动粗细／透明度预览、面积擦、墨迹擦和套索擦。
- 白板提供分页与逐页纸型、缩略图导航和页面管理，支持图片、矢量图及视频。矩形／套索选择、缩放旋转、拖动克隆、图层、形状顶点、填充与线型用于整理板书，墨迹可在当前画布中回放。
- PPT 工具跟随放映与阅读场景调整位置，按幻灯片切换批注。PDF 模块提供连续／双页阅读、触摸滚动、目录／缩略图／搜索、跨页书写、矢量保存和按页导入白板。
- 数学模块将二维／三维构造、代数对象、CAS、公式键盘与手写候选放入原生工作区，支持参数联动及图形导出。计算和指令边界见 [解析几何说明](ANALYTIC-GEOMETRY.md)。
- AI 插件提供多供应商连接、四类 API 协议、模型选择、附件、流式多轮会话和画布工具调用，并对接模块工具、可选 MCP 及存档命名建议。真实服务需要配置，案例截图采用视频工程保存的真实 DeepSeek 回复，并注明原样回放取景。
- 课堂工具提供倒计时、秒表计次、多人点名和软冷却抽样。文稿库提供缩略图、搜索、收藏与预览，保存设置支持自动保存、退出保存、截图底图存档和中断续接。云同步可选连接自行部署的服务。
- 设置具有搜索与分类导航，已安装插件注册自己的相关设置入口。应用和插件下载复用更新源管线，MSI、便携包和运行时脚本用于学校部署。

28 页 A4 报告覆盖上述功能，并补充原生截图、全国一卷第 18 题椭圆推导、立方体截面、六段 Windows 演示与三段鸿蒙操作录屏，以及码道交互、代码重构和排错材料。React 网页可离线阅读和播放片段，PDF 通过浏览器打印生成。[产品说明书 PDF](../deliverables/product-manual/out/InkCanvas-产品说明书-A4.pdf) · [可编辑工程与使用说明](../deliverables/product-manual/README.md) · [内容依据](../deliverables/product-manual/CONTENT-EVIDENCE.md)

## 鸿蒙原生适配与验证范围

鸿蒙分支使用 ArkTS／ArkUI，围绕 core、ink、platform、controls、features 和 entry 分层实现应用内画布、工具条、选择擦除、笔锋与手势等能力。C# 黄金向量用于核对可迁移算法和文件契约。跨应用批注、PPT COM 控制、全局热键和更新入口根据鸿蒙的平台能力调整为应用内批注、课件／PDF 场景、应用内快捷键与市场更新路径。

2026-11-18 至 19 日移植记录提供早期逻辑互认与 HAP 构建依据。2026-11-24 新补入用户提供的三段鸿蒙操作录屏，显示连续书写、笔色与笔锋切换、工具栏折叠、化学环与键线、白板背景和生物传感器 PDF 阅读批注。配套码道截图与日志报告 MatePad／HarmonyOS 7.0.0.111 的安装和测试，包含工具栏外观重构、面板跟随／钳制、编译和 OCR 反馈。

本次按用户提供材料核对所示操作，未重新连接设备测量端到端延迟、帧率或功耗。OCR 手写空结果、多窗／分屏覆盖、跨应用悬浮权限、PDF 写回保真与完整解析几何内核继续作为明确边界记录。[新增鸿蒙材料说明](HARMONY-EVIDENCE-20261124.md) · [早期移植记录](harmony-porting-notes.md) · [鸿蒙 AI 魔法套索](ai-magic-lasso.md)

下文的运行依赖、安装脚本和部署步骤适用于 Windows 版本。鸿蒙签名、设备安装与市场发布依据相应平台流程另行验证。

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
.\tools\install-runtime.ps1 -MsiPath .\InkCanvas-WinUI3-3.15.0-win-x64.msi
```

从发布 ZIP 中执行时，将上述 `tools` 路径替换为 `Tools`。从 Release 单独下载脚本时，直接运行 `./install-runtime.ps1`。安装系统运行时可能触发 UAC，应用 MSI 采用当前用户安装。

离线教室可预先下载 Microsoft 官方 .NET 10 x64 Runtime 安装程序与 Ink Canvas MSI，并提供本地路径。脚本校验运行时安装程序的 Microsoft 数字签名，执行后再次检查所需运行时。

```powershell
.\tools\install-runtime.ps1 `
    -RuntimeInstallerPath .\dotnet-runtime-10.0.0-win-x64.exe `
    -MsiPath .\InkCanvas-WinUI3-3.15.0-win-x64.msi
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
Get-FileHash .\InkCanvas-WinUI3-3.15.0-win-x64.msi -Algorithm SHA256
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
msiexec.exe /i "InkCanvas-WinUI3-3.15.0-win-x64.msi" /qn /norestart
```

需要自定义安装位置时可传入 `INSTALLFOLDER="目标目录"`。部署后分别验证写字、擦除、白板翻页、PDF 打开、多屏定位与 PPT 切页。日志位于 `%APPDATA%\InkCanvas\Log.txt`，主配置位于 `%APPDATA%\InkCanvas\Settings.json`，文稿保存位置可在设置中调整。

### 从源码构建并运行

```powershell
git clone https://github.com/QiXinia/Ink-Canvas-HEHS.git
Set-Location Ink-Canvas-HEHS
git checkout v3.15.0

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

客户端安装与本地白板运行完成后，可按需配置云同步。服务端使用 FastAPI、数据库与私有对象存储，部署流程及环境变量模板随源码提供。准备 Docker Compose、HTTPS 反向代理和对象存储，按服务端文档设置仓库外的环境文件，再执行 `docker compose up -d --build`。在客户端云同步插件中填写自己部署的 HTTPS 地址并登录。[服务端部署说明](https://github.com/QiXinia/Ink-Canvas-HEHS/blob/v3.15.0/server/cloud-sync/README.md)

部署实例的域名、IP、账号、令牌及对象存储密钥单独交付，公开文档与发布包使用通用配置模板。

## 三、额外使用的开源组件

项目保留 Ink Canvas Artistry 与 Ink Canvas 的上游贡献及 GPL-3.0 许可声明。以下按当前源码中的直接依赖和随包素材列出用途，具体条款以仓库及 NuGet 包附带的许可证为准。[项目许可证](https://github.com/QiXinia/Ink-Canvas-HEHS/blob/v3.15.0/LICENSE) · [Ink Canvas Artistry 上游](https://github.com/InkCanvas/Ink-Canvas-Artistry) · [Ink Canvas 上游](https://github.com/WXRIW/Ink-Canvas)

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
| LobeHub 与 OpenCode 图标 | 仓库随附 SVG | AI 服务商识别图标 | 随素材附带的 MIT 许可与品牌来源说明。[供应商图标说明](https://github.com/QiXinia/Ink-Canvas-HEHS/blob/v3.15.0/src/InkCanvas.Modules.AiAssistant/Assets/Providers/README.md) |

解析几何的传递依赖还包括 ANTLR、Acornima、GenericTensor、HonkSharp、Numbers 等，许可文本随模块的 `Licenses` 目录分发。图片、Markdown 和图标相关许可位于主程序 `Assets/Licenses`，PDF 与几何模块的许可位于各自 `Licenses` 目录。

可选云同步服务使用 FastAPI、Uvicorn、SQLAlchemy、psycopg、pwdlib、PyJWT、pydantic-settings 与 oss2。版本范围由服务端依赖文件管理，使用范围与服务部署有关。[服务端依赖清单](https://github.com/QiXinia/Ink-Canvas-HEHS/blob/v3.15.0/server/cloud-sync/requirements.txt)

## 四、码道在项目开发中的应用总结

开发中使用华为云码道 CodeArts 代码智能体，以原始 Prompt、参考截图和设备反馈驱动鸿蒙功能迭代。用户提出跟随工具栏、折叠后保留常用功能、图标清晰且不重复、化学绘制与批注共存等要求，智能体读取相关目录、修改目标文件、执行构建，并根据反馈继续排错。

| 实践环节 | 材料中的具体过程 | 对应证明 |
| --- | --- | --- |
| 核心功能与 AI 交互 | 公式识别／搜题优化、设置面板跟随与折叠、化学结构绘制等原始提问及回复 | CA-03、CA-04、CA-06、R-01 至 R-03 |
| 代码生成 | 读取图标目录与映射，生成码点检查脚本；增加公式结果字段和化学输入门控 | glyph-dump.js、AiAssistant.ts、AiStudio.ets、Index.ets 的 write／edit 摘录 |
| 界面与逻辑重构 | Windows／鸿蒙工具栏设计对照，CompactCard、面板位移和边界钳制，化学层让出墨迹输入 | CA-02、CA-05、R-02、R-03 |
| 编译排错 | 保存 ArkTSCompilerError／BUILD FAILED 与后续 BUILD SUCCESSFUL 工具输出 | D-01 |
| 触摸排错 | 透明命中容器使拖动落入画布，按工具栏实际范围拦截事件，记录拖动后书写与清空复测 | CA-07、D-02 |
| 验证与边界 | 25 项逻辑断言通过记录、OCR 引擎不可用／手写空文本反馈、模拟器与设备结果分别说明 | CA-06、CA-08、D-03、HM-01 至 HM-03 |

材料整理为独立文件夹，含 8 张交互截图、6 份过程案例、JSONL 日志选段、三段运行录屏及来源索引。日志按原文件名和行号回溯，原包 SHA-256 单独保存。截图中的设备标识已遮盖，摘录中的用户目录、网络地址等按规则处理。原日志自身的 `****` 保留，部分非标准 JSON 按可见字段截取，未补写缺失内容。[华为云码道使用证明材料](../deliverables/华为云码道使用证明材料/README.md) · [可视化审看入口](../deliverables/华为云码道使用证明材料/index.html)

码道支持需求细化、代码生成、重构和排错。开发者继续通过设备反馈、构建与逻辑检查核对结果。OCR 精度、设备性能、跨应用权限及复杂多窗场景按实际证据标明限制。

### 成员补充与新标志

艾尼萨尔补充 5 张码道截图和两份开发记录，涵盖流式网关函数解释、pageIndex 连续性校验、poly 参数与 bat 编码排错。原始文档和截图归档在证明材料的“06-艾尼萨尔补充”，测试及效率数字按作者报告标注，本次未重新运行其代码。材料合计 13 张截图。

应用采用用户提供的白板与笔画新标志，浅色模式为深轮廓与蓝色笔画，深色模式为浅轮廓与亮蓝笔画。Windows EXE、安装与快捷方式使用适合不同桌面背景的图标，原生窗口随实际主题切换。
