# 板中板（子展板）改造方案与 AI 助教显示修复

- 分支：`新增展板板中板并修复AI助教显示问题`
- 基线：`076f5d4`（已合并云端 `feat/winui3-migration` v3.7.5，本地功能优先）
- 技术栈：C#/.NET 10 + WinUI 3 + Win2D；不改 ArkTS/Harmony

## 1. 现状审查与根因

### 1.1 AI 助教在展板中不显示

| 现象 | 根因 | 处理 |
|---|---|---|
| 展板内无触发点 | 合并后浮条主栏「AI 识别」丢失；`BoardWindow` 三条工具栏与「更多功能」均无 AI；进板后浮条被隐藏；无快捷键 | 恢复浮条主栏 AI 按钮；展板中栏与更多面板加「AI 助教」；新增 Alt+A（全局 + 板内兜底）；子板工具栏加入口 |
| 浮窗创建但不可见 | overlay 模式曾把 owner 设为 `LayeredInkWindow`（Win32 分层窗口，非 WinUI AppWindow），`AppWindow.Show()` 静默不显示 | overlay 模式 owner 置空；板/子板模式 owner 设为对应 WinUI 窗口；Show 后 `EnsureAbove` |
| 定位/截图错位 | 定位固定主屏工作区 + Overlay DPI；截图把画布客户区坐标当屏幕绝对像素 | 按目标窗口所在显示器取工作区/DPI/客户区原点，截图坐标改为屏幕绝对物理像素 |
| 异常被吞 | `OpenAiAssistant` 无顶层 try/catch | 入口与提示路径加异常记录与用户提示，不静默 |

### 1.2 板中板现状

- `BoardService` 可多实例，`InkController`/`InkSurface`/`BoardDrawingLayers`/`BoardImageLayer`/`BoardMediaLayer`/`BoardPageNavigator` 均可复用，无单例画布依赖。
- 原有单板假设：`ShellController._board/_recorder/ActiveController`、AI 目标、时光机事件只有 `PageIndex`（无板标识）、保存文件无子板节点。

## 2. 改造方案

### 2.1 架构

`SideBoardWindow`（独立 `OverlappedPresenter` 窗口）内部组合：

- `AppServices.SideBoard`：独立 `BoardService`（独立页、页历史、选区、视口、图片/媒体）
- `InkSurface`（绑定子板 `InkController`）+ `BoardDrawingLayers` + `BoardImageLayer` + `BoardMediaLayer`
- `BoardPageNavigator`（复用缩略图/翻页）
- 纸背景 `CanvasControl` + 简化工具栏

复用现有渲染/输入/保存链路，不复制白板逻辑。

### 2.2 交互

- 入口：浮条主栏、展板中栏、更多面板、子板工具栏；快捷键默认 Alt+N（设置可改）。
- 单实例、非模态、右侧抽屉；打开/收回动画，不改变主展板布局。
- 位置/尺寸记忆（设置 `sideBoard.x/y/w/h`），越界回退默认右侧抽屉；工作区/DPI 变化自动夹取。
- 收回保留内容（Hide 不销毁）；清空需单独操作；主展板关闭/应用退出时关闭子板并清理。

### 2.3 状态隔离

- 主/子板各自 `BoardService` 与 `InkController.History`；`ActiveController` 按激活窗口分流。
- 拉出/收回/移动/缩放不进入撤销栈；只有内容修改进入所在板当前页 History。
- 子板输入不触发主板书写/框选/翻页。

### 2.4 贴到主展板

- 子板当前页离屏渲染为透明 PNG（2x，内容边界 +16 DIP padding，上限 8192px）。
- 插入主展板当前页为 `BoardImageAsset`，居中且宽度不超过可见视口 60%，等比缩放。
- 走 `BoardService.AddImage` 的 `DelegateAction`，一次插入对应一步撤销；默认复制，不清子板。
- 主展板未打开时明确提示（不强制切板，避免 owner/目标失效）。

### 2.5 AI 目标路由

- 发起时记录 `AssistantTarget`（Overlay/主 Board/SideBoard + 页索引 + 页对象 + 会话 ID）。
- 截图、锚点、贴回、收藏来源全部按目标板；写回要求同板 + 同页 + 同页对象 + 同会话。
- 过期响应丢弃并记日志；目标失效明确提示。
- 子板收回：AI 浮窗 `AppWindow.Hide`（保留会话）；再拉出恢复；子板关闭/主展板关闭/退出时关闭并停流。

## 3. 数据结构变更（格式 v4）

- `BoardFile.Version = 4`；新增 `SideBoard { Version, PageIndex, Pages[] }`，页结构复用 `BoardPageFile`。
- `Blob` 引用新增 `Scope`（`main`/`side`），主/子板共用 `media/NNNNNN.bin` 去重；`Data/PosterData` 仍不入 JSON。
- 兼容：ZIP 接受 v2/v3/v4；非 ZIP 旧 JSON 接受 v1..v4；无 `SideBoard` 节点时子板清空、主展板正常；旧单页 JSON 降级逻辑保留。
- 设置新增：`hotkey.sideBoard`（默认 Alt+N）、`sideBoard.x/y/w/h`（默认空）。
- 页名与 `ViewTransform` 修复为随页保存（此前缺失导致重开丢页名/视图）。

## 4. 验收证据

### 4.1 构建与自动化测试

- `dotnet build InkCanvas.slnx -c Debug -p:Platform=x64`：0 警告 0 错误。
- 测试合计 **1514 项全通过**：
  - Core 1023、Ai 144、P0 47、Classroom 297、LegacyImport 3。

### 4.2 运行时诊断 `--sideboardcheck`

命令：

```
InkCanvas.exe --sideboardcheck=D:\hw\merge-backup\sidecheck2
```

结果：`exit=0`，11/11 PASS（1 项可选），关键输出：

```
[PASS] sub-board ink survives hide and show  strokesBefore=2 strokesAfter=2
[PASS] sub-board ink and undo leave the main board untouched  mainStrokes=0 mainCanUndo=false mainUndoCount=0
[PASS] sub-board undo and redo keep the main history untouched  afterTwo=2 afterUndo=1 afterRedo=2 mainUndoAfter=0
[PASS] paste to main board inserts one image and undo removes it  imagesBefore=0 imagesAfter=1 imagesAfterUndo=0
[PASS] save and reopen restores side pages, strokes and page index  mainPages=1 sidePages=2 sideIndex=1 sideStrokes=2
[PASS] v3 package without a side node loads with an empty sub-board  sidePages=1 sideStrokes=0
[PASS] 50 animated show/hide cycles keep sub-board strokes and visibility  failures=0 roundsMeasured=50 slowestRoundMs=499 handles 1278→1371
[PASS] offscreen placement is clamped into the work area  after=0,0 450x350 workArea=0,0 1920x1020 dpi=1.25
[PASS] bottom-right overflow is clamped into the work area  after=1470,670 450x350
[PASS] offscreen placement is clamped at 150% app scale  affectsSideBoardLayout=false
```

最终运行：`summary: 22/22 passed, 0 failed, 1 optional, total=26349.7 ms`，退出码 0。新增断言：
- 贴回数值：资产 `692.35,386.35 151.3x91.3`，视口 `1536x864`，中心偏差 `0px`，宽度占比 `9.85%`（上限 60%），比例误差 `0`。
- 视口/选区隔离：子板缩放 1.35 + 平移 (36,-24)，主板 `ViewTransform` 保持单位矩阵；子板有选区时主板 Selection=0。
- 句柄阈值：50 轮增长 `+135`（阈值 300）；500 轮长稳 `+41`，0 失败（约 237s）。
- 真实鼠标黑盒：拖动标题栏 `moved=True`；右下角 resize 至 584×584（≥450×350）；拖出屏幕至 (-400,-300) 后夹回 `0,0 976x518`，完整落在工作区。

产物：`sideboard-check.txt`、`sideboard-check.json`、`side.icink`、`side-v3.icink`、`sideboard-show.png`。

### 4.3 实机日志/截图

- 主板 AI：`[AI] ShowAt 后：isVisible=True`。
- 子板 AI：`target=kind=SideBoard,page=0`；截图 `px=1161,py=153`（锁定子板客户区）；`owner=子板 HWND`。
- 子板收回：`AppWindow.Hide 后：isVisible=False`；再拉出 `恢复 AI 浮窗后：isVisible=True`。
- 子板窗口截图：标题「板中板」、深色画布、底部工具栏（笔/荧光笔/橡皮/清空/背景/撤销/重做/翻页/加页/贴到主展板/AI/收回）均正常。
- 关闭板流程后 `error.log` 今日 0 条新异常（历史记录停留在 2026-11-21）；关闭板后浮条与 Overlay 正常恢复。
- AI 端到端（实机，主板）：浮条「AI 识别」→ 浮窗 3 秒内状态为「已完成」并输出分步引导内容（本机 mock 流），按钮齐全（分步引导/基础变式/提高变式/追加选区/贴回白板/收藏例题/下一步/复制/发送）；`WindowPattern.Close` 后窗口从 UIA 树消失。
- AI 状态机（`--aicheck`，本地注入不联网）：6/6 PASS —— 加载态「识别中」+进度环、错误态「出错」+错误文本+2 个可用「重试」、UIA 触发重试后 `Error→Recognizing`、本地 Token+Done 到「已完成」、关闭后 `closedCount=1` 且 `AppWindow` 释放、状态回 Idle。
- PowerPoint 放映回归（`--pptcheck`）：全屏与阅读视图两种放映类型各 9 项、共 **18/18 PASS**，退出码 0。修复的真实回归：
  - 导航节流（180ms）导致紧随 PptPrevious 之后的 PptNext 被静默丢弃 → 改为节流窗口内排队补发一次；
  - 阅读视图窗口尺寸变化导致 `OverlayInkPresenter` 旧尺寸位图复制崩溃（Destination too short）→ 按窗口尺寸重建缓存；
  - 退出放映未恢复进入前工具栏位置（先重排后捕获基线）→ 改为先捕获基线再重排。
- UIA 编码核实：将 PowerShell 控制台输出编码设为 UTF-8 后，子板窗口 UIA Name 读取为「板中板」，与源码字节、实机截图一致；此前乱码属控制台编码/元素缓存显示差异，非应用缺陷。

## 5. 差距清单（未达成或未验证，不得视为已验收）

1. **子板时光机**：本期不录子板事件，仅保证内容随主文件保存与最终状态恢复；回放不区分主板/子板。**用户已确认接受此差距（2026-11-22），后续如需回放单独立项。**
2. **视频贴回**：子板仅含视频时按“无可贴回内容”处理（视频不参与离屏合成）。
3. **动画路径压力**：animate:true 50 轮与 500 轮长稳均 0 失败（句柄 +135/+41，阈值 300），但未做逐帧观感/明显闪烁判定；长稳为 500 轮窗口动画，不等同 90 分钟课堂压力。
4. **多屏/系统高 DPI/高对比度**：当前环境单屏 1920×1080@125%；真实鼠标拖动/缩放/拖出屏幕夹回已实机验证，但跨物理显示器拖动、系统级 150%/200% DPI、高对比度未实机（应用内 150% 缩放不参与子板窗口布局，已在诊断中标注）。**用户已确认作为环境限制接受。**
5. **WPS 放映**：本机未安装 WPS（无 wpp），仅完成真实 PowerPoint 放映回归（18/18）；WPS 的 COM/窗口检测未验证。**用户已确认作为环境限制接受。**
6. **真实触控/手写笔**：诊断为合成指针（Pen，压力 0.5），未验证真实压感/倾斜/掌拒/双指手势下的隔离。**用户已确认作为环境限制接受。**
6. **真实触控/手写笔**：诊断为合成指针（Pen，压力 0.5），未验证真实压感/倾斜/掌拒/双指手势下的隔离。
7. **动画观感**：抽屉滑入/滑出与 AI 浮窗联动时序未做逐帧观感验证。
8. **UIA 读屏**：按钮已设置 AutomationName；PowerShell UIA 读取中文显示乱码为客户端控制台编码问题，源码字节与实机截图正常（UTF-8 控制台下 UIA Name 读取为「板中板」），读屏软件实际朗读未验证。
9. **AI 错误/重试**：实机仅验证 mock 成功流与关闭；错误注入与重试按钮点击路径由 `Ai.Tests`（状态机/目标策略/布局 144 项）覆盖，未做实机错误注入。
9. **v3 兼容样本**：诊断中的 v3 文件由 v4 降版本重打包生成，非真实历史 v3 文件样本。
10. **实机文件对话框保存/重开**：保存/重开由 `--sideboardcheck` 直接调用 BoardService 验证；未走人工文件对话框流程。

## 6. 运行方式

- 正常使用：`D:\hw\Ink-Canvas-HEHS\artifacts\app\InkCanvas.exe`
- 诊断：`InkCanvas.exe --sideboardcheck=<输出目录>`