# WPF 菜单、调色盘与设置迁移对照

> 本文件区分 **源码确认**、**已接线** 和 **未验证**。按用户明确要求，菜单/PPT/WPS/白板/DPI 行为验证跳过；只做 Release 编译检查。自交填充为单独的例外，见文末。

## 原始来源

- 上游 commit `8b2c95b`：`Ink Canvas/MainWindow.xaml` 的设置、调色盘与工具菜单；`Resources/Settings.cs` 的字段和默认值。
- `MainWindow_cs/MW_Settings.cs` / `MW_SettingsToLoad.cs`：设置提交与加载。
- `MW_PenColors.cs` / `MW_PenHighlighterColors.cs`：颜色顺序与主题色。
- `MW_PPT.cs`：放映切页、页码按钮、隐藏页/自动计时通知、墨迹与截图保存。

## 调色盘逐项对照

实现：`src/InkCanvas.Controls/Controls/PenPalette.cs` + `src/InkCanvas.Core/Settings/OriginalMenuCatalog.cs`。

| 原选项 | 原值 / 顺序 | WinUI 迁移 |
|---|---|---|
| 普通笔颜色 | 黑、白、红、粉/紫、橙、黄、绿、青、蓝；浅/深主题 RGB 来自 `MW_PenColors.cs` | `OriginalMenuCatalog.PenColors`；可跟随主题切换 |
| 荧光笔颜色 | 红、橙、黄、青、蓝、紫 | `OriginalMenuCatalog.HighlighterColors` |
| 笔迹风格 | **三项**：`点集笔锋`、`速率笔锋`、`关闭笔锋`，索引 0–2（不存在第四项） | 逐项映射 `Tail/Velocity/None` |
| 画笔宽度 | 原菜单 1–30、步长 1；持久化值 = 菜单值 / 2，即 0.5–15、步长 0.5 | 调色盘显示原菜单值，callback 收到持久化单位 |
| 荧光笔透明度 | 20–240、步长 20；默认 `Canvas.InkAlpha=140` | `HighlighterAlpha` / `InkAlphaChanged` 回调；连到每条 `StrokeAttributes.HighlighterAlpha` 和 `.icink` 持久化 |
| 原生 ColorPicker | 上游没有 | 不伪称原版选项；颜色来自固定色板 |

调色盘现在是**独立 WinUI 窗口**，不是浮动栏 `Flyout`：WinUI Flyout 被短工具栏宿主窗口裁切，颜色菜单看似无法打开。画笔/荧光笔各自弹独立 palette；使用 Segoe Fluent Icons 字体文件和矢量鼠标图标，不依赖客户端偶然安装的同名字体。

## 原 Settings 字段与当前菜单状态

当前 `AppSettings` 中的原始 `Canvas / Gesture / Startup / Appearance / PowerPoint / Automation / Advanced / InkToShape / Rand` 字段都应在 SettingsWindow 中有对应行。可真实接线的行保存并调用 `AppServices.ApplySettings`；有字段但实现未迁移的，显示值与缺口说明，**不是可点但没作用的假开关**。

仍明确未迁移或未完整接线的 WPF 行为：

- `IsCompressPicturesUploaded` 图片上传压缩；`IsShowCursor` 光标；`HideStrokeWhenSelecting` 在所有选择/鼠标态的完整原行为；`HyperbolaAsymptoteOption` 图形生成选项。
- `AutoSwitchTwoFingerGesture` 自动切换；完整选区/画布变换中心交互。
- `IsEnableDisPlayFloatBarText`、彩色工具栏、按钮背景透明、工具栏按钮显隐、笔尖模式切换器；画板独立缩放。
- 静默更新 UI 时间选择与自动静默安装策略；「开机时默认收纳」的进程启动恢复状态尚未接入。
- PPT 的 `IsNoClearStrokeOnSelectWhenInPowerPoint` 规则及 `IsEnableTwoFingerGestureInPresentationMode` 门控仍未按 WPF 所有状态逐条接线；页码/导航面板、WPS COM 与切页墨迹缓存已接入，但**未经行为验证**。
- 自动收纳的全部厂商/进程识别仅有一部分；ignore-desktop-annotation、轻白板、鸿合/文香/中原等识别项未完整移植。
- 自动查杀进程、退出书写模式自动清屏、旧存档清理的完整调度、关闭前确认、目录浏览器、触摸尺寸测量 UI。
- 历史配置中注释掉的 `IsAutoFoldInZySmartBoard` 不在当前 model 中；没有擅自加字段。

完整 JSON 字段定义仍见 `src/InkCanvas.Core/Settings/AppSettings.cs`。这份表记录的是功能差异，不是声称所有 WPF 边缘逻辑都逐像素/逐状态等效。

## PPT 菜单映射说明

- `IsShowPPTNavigationBottom` / `IsShowPPTNavigationSides` 控制**页码/当前位置按钮**，不是箭头面板；当前两组标签已经按原意称「页码按钮」。
- `IsShowBottomPPTNavigationPanel` / `IsShowSidePPTNavigationPanel` 控制独立底部/侧边箭头导航面板。
- 具体 COM、每页墨迹、确认提示和 `.icstk/.icart` 遗留格式差异见 [`ppt-porting-notes.md`](./ppt-porting-notes.md)。

## 白板菜单

当前白板重建三栏底部工具、分页、每页墨迹历史、图形、擦除、图像对象、保存/打开；与原 WPF XAML 不逐像素一致。源文件、对应关系、媒体对象/旧 ISF 等明确缺口见 [`board-porting-notes.md`](./board-porting-notes.md)。

## 验证声明

- 本轮按要求**不运行单测、不启动应用、不与 Office/WPS/触屏交互**；Release 编译检查为 0 警告、0 错误。
- 为处理「同一笔自交的笔迹重叠产生布尔镂空」问题，单独写并运行 3 项纯几何回归测试（3/3 通过）；这只验证 capsule winding 方向/轮廓组织，**不代表 Win2D GPU 实际合成已实机验证**。
- 不同 DPI、DWM 阴影、Flyout 替代窗口、PowerPoint/WPS 与希沃设备仍需在目标 Windows 客户端和硬件上验证。