# HarmonyOS 移植说明（进行中）

2026-11-24 补充说明见 [鸿蒙新增运行与开发材料](HARMONY-EVIDENCE-20261124.md)，包含用户提供的设备操作录屏与码道记录。本文保留早期移植阶段的原始状态与历史验证范围。

更新时间：2026-11-18（Phase A 执行中）
设计规格：`docs/superpowers/specs/2026-11-18-harmonyos-port-design.md`
实施计划：`docs/superpowers/plans/2026-11-18-harmonyos-phase-a-foundation.md`

## 验证状态（随任务推进更新）

| 层 | 手段 | 当前结果 |
|---|---|---|
| L1 逻辑互认 | C# 生成黄金向量 + Node 运行 ArkTS | **当前：423 用例 / 43 个向量文件全部通过（Phase A–G + 评审修复 + C# 演进同步 + 缺口补齐）** |
| L2 编译 | `hvigorw assembleHap`（26.0.1 Beta1 SDK） | 通过（未签名 debug HAP，73 tasks） |
| L3 运行/UI | 真机 | **未实机验证**（本机无设备；模拟器不可靠） |
| L4 基线回归 | `dotnet test`（向量子集） | 30/30 通过；codelinter 0 错误 / 6 警告（性能建议）；完整 WinUI3 套件未在本轮重跑 |

### Phase A 已交付（纯逻辑核心）

`harmony/core`：墨迹模型/颜色/属性、因果笔锋（Tail/Velocity）、胶囊并集轮廓、线条擦、
撤销栈、矩阵（行向量）、选区几何、双指手势、枢轴、25 种图形、形状识别、触摸分档、手掌判定、
回放时钟/会话、预乘 BGRA 合成、主题判定、触摸设置模型与 Settings 编解码、.icink/画板包/PPT manifest 编解码
（含 ObjectFrame 与画板 HighlighterAlpha 保留、99 页上限）、InkDocument 命中/面积擦/边界、InkStroke bounds 膨胀与 setter 重算、
版本/Release 解析/更新静默窗口策略。

- 功能单元：manifest 中 14 个 Phase A 单元状态 `ported`；`system-autostart` 唯一 `not-ported`。
- 锚点：29 个 ArkTS→C# 锚点（C# 侧反向锚点待用户工作树清理后补齐）。
- 覆盖率（Phase A 范围）：14/14。
- 独立评审修复轮：1 Critical（ObjectFrame/画板 HighlighterAlpha）+ 7 Important（bounds/setter、面积擦与文档命中、99 页上限、manifest/schema 收口、checker 假绿）已修复并回归。

### 模拟器环境（2026-11-18）

- 镜像与实例已就绪：API 26 平板镜像下载成功；实例 `InkPad26`（E:\harmony-cache\emu-instances，MatePad Pro 13）。
- **启动失败**：`ErrorCode 00801001 未开启 Hyper-V`。宿主 `systeminfo` 显示 hypervisor 存在但 Windows 可选功能不可用（`Get-WindowsOptionalFeature: 没有注册类`），本机无法启用 Hyper-V/嵌套虚拟化。
- 结论：本机**无法进行模拟器运行验证**；L1 向量与 L2 构建继续为准，模拟器/真机验证待具备虚拟化的宿主执行。
- HAP 安装另需签名（OpenHarmony 调试证书材料已在 SDK `toolchains/lib`，待模拟器可用时配置）。

### 仍需后续阶段（B–G）

- 已完成：platform compat/输入归一化/双层 Canvas 渲染、controls token/浮条/调色盘/图形菜单、entry 主页面壳。
- 已完成：InkController 状态机/手势/选区（B2/B3）并接入 entry 触摸输入；画板分页/元素变换/保存语义（E1）。
- 已完成：课件(PDF)/计时/抽奖/回放、设置面板/指南/是否窗、画板模式（分页/背景/插图/视频/保存打开）、应用内快捷键/沉浸式/Desmos/更新提示、manifest 56 项收口（98.21%）、api-matrix 补全。
- 覆盖率：**55/56 = 98.21%**（32 ported + 23 adapted + 1 not-ported=开机自启）。
- **平台禁用决策（2026-11-19 用户拍板，不再制作/迁移）**：跨应用悬浮批注（应用内 Canvas 承担）、PPT 放映控制（WPS 跳转/PDF 批注写回承担）、全局热键（应用内快捷键保留）、自动收纳（手动收纳保留）、静默安装、内置升级（市场更新唯一路径）、进程查杀、边缘手势抑制——设置面板对应行已收敛为禁用说明；manifest 中自动收纳与手势抑制改记 not-ported（覆盖率口径 55/56→53/56=94.64%，为诚实反映禁用决策）。
- 开机自启：唯一既有 not-ported（用户此前已跳过）。其余功能子缺口已补齐。运行时行为（WPS 响应/PiP 空 surface/市场弹框/弹簧曲线/matrix4/压感/SymbolGlyph/视频首帧/PDF 写回保真）待真机核对。
- **GitHub Actions 自动构建已关闭**（.github/workflows/dotnet-desktop.yml 仅保留 workflow_dispatch 手动触发）。
- 已补齐（2026-11-18 缺口轮）：设置全量 88 字段、更新日志窗口、系统材质/blur（API26 门控）、画板图片 matrix4 精确变换、视频进度保持/续播、SymbolGlyph 46 项映射（文字兜底）、浮层/浮条动效。
- 已补齐（2026-11-18 缺口轮 2）：倒计时圆环进度/紧凑/全屏/结束提示音（TimerDownNotice.wav）、抽奖滚动闪烁/替换名单（old-->new）/单次模式/多人抽选/分组输出（LotteryRoll 向量）、画板图片拖拽手势（Select/Pan 命中接管 + BoardElementHistory 单撤销单元，向量）、动效升级弹簧曲线 curves.springMotion + 共享元素 geometryTransition。弹窗界面按 ArkTS 设计特色差异化（用户授权）。
- 已补齐（2026-11-18 缺口轮 3，平台对接调研后落地）：PPT 放映→WPS 鸿蒙版（WpsInterop：文件打开 Want + UTD + FLAG_AUTH_READ，手机/Pad 包自适应，16000019 回退引导安装；无公开放映 intent，放映控制待 WPS 开放）；自更新→市场更新（MarketUpdate：updateManager.checkAppUpdate/showUpdateDialog + AppGallery appdetail 详情页）；顶层置顶→倒计时画中画悬浮窗（CountdownPiP：PiPWindow typeNode + customUIController 圆环数字，三方免权限，系统播放/暂停复用为暂停/继续，退桌面自动拉起）。调研依据 docs/superpowers/research/harmony/14/15。
- 已补齐（2026-11-18 缺口轮 4，代码缺口+布局精细+基线功能 ArkTS 实现）：上传压缩至 1080P（CompressMath 向量+ImageCompress）、画板 UI 缩放、手指模式切换按钮、退出按钮+二次确认（terminateSelf）、鼠标模式自动清屏、自动开关两指缩放、调色盘笔尖模式切换（PenNib 圆/椭圆45°）、画笔光标（跟随指针指示环）、触摸校准垫（TouchCalibration 向量+采集面板）、课件选择时墨迹隐藏开关；布局精细化：画板三独立浮条（页面/背景/文件）、PPT 四导航面板（底部+侧边翻页/页码）、断点感知面板宽度（手机 360/平板 440）。原「与基线一致」缺项按用户要求以 ArkTS 实现推进。
- 模拟器/真机验证：本机 Hyper-V 不可用，待具备虚拟化的宿主或真机执行。

## 跨架构契约对齐（2026-11-19）

- manifest 的 csharp 路径与真实文件对齐：`ink-erase` → `InkDocument.cs`（无 StrokeEraser.cs）、`ui-shape-picker` → `PickerWindows.cs`（无 ShapePreview.cs）、`ui-yes-no` → `ShellController.cs`（ContentDialog，无 YesOrNoWindow）、`ui-theme-tokens`/`platform-desktop-acrylic` 去掉不存在的 `PopupBrushes.cs`、`tool-launchers` → 同层 `SystemServices.cs`（**C# 侧无启动器实现**，如实注明）、`board-video` 去掉不存在的 `BoardMediaLayer.cs`（**C# 基线仅图片对象，无视频层**）。
- 5 处 ArkTS `@parity` 锚点同步改指，`parity/anchors.json` 71 对与扫描结果一致。
- `parity/glyphs.json` 严格对齐当前 `Glyphs.cs`（29 项）；C# 已移除的 27 个历史图标移入 `parity/glyphs-legacy.json`，保留 Phase D 的 target 映射供 `IconGlyph` 参考。
- 验证：`node tools/parity-check.mjs` 通过（71 锚点），`--final` 通过；456 向量 / 47 文件全部通过。未改动 C# 功能代码；`tool-launchers` 与 `board-video` 在 C# 侧仍未移植。

## AI 魔法套索与助教浮窗（2026-11-19）

- 鸿蒙侧新增 feature `ai-magic-lasso`（manifest 57 项，覆盖 56/57 = 98.25%，状态 `adapted`；C#/WinUI3 未回移）。
- 目录：`core/assistant`（状态机、套索几何、锚点翻转/跟随、LaTeX/Markdown 渐进解析、贴回模型、SSE 解析 8 文件）、`platform/assistant`（配置、流式客户端、选区截图 base64 3 文件）、`controls`（MagicLassoEntry / AssistantCard / StreamMessage）、`entry/Index.ets` 接线（套索采集、浮窗跟随、贴回 undo、Q/Esc）。
- 请求 6.1 字段与 SSE 6.2 事件完整实现；零第三方依赖；`AI_ENDPOINT`/`AI_MODEL`/`AI_TIMEOUT_MS` 配置，端点为空时走内置 mock 流。
- 验证：`hvigorw assembleHap` **BUILD SUCCESSFUL**（未签名 debug HAP，约 2.2 MB）；480 向量 / 54 文件通过；`parity-check --final` OK（85 锚点）；`tools/mock-ai-sse.mjs` 实测输出 token/step/formula/done 事件序列。
- 未验证（如实记录）：真机 2 秒首字、Acrylic 实机效果、焦点陷阱/200% 缩放、暗色观感、真实网关的 `requestInStream` 分块节奏；图形标注仅文本提示，未绘制到图层。详见 `docs/ai-magic-lasso.md`。

## 部署与验证工具（2026-11-18）

- 本机（ECS，无嵌套虚拟化）**已移除模拟器相关文件**：system-image（4.21GB）、Emulator26.0 配置、实例目录、CLI emulator 程序；保留 hvigor/ohpm/SDK/hdc 构建链。
- 真机安装验证：`tools/device-install.ps1`（安装+启动+截图+hilog）、`tools/device-verify.ps1`（冒烟+崩溃检查+15 项人工清单）。
- 物理机模拟器部署+联调：SKILL `.claude/skills/harmony-emulator-setup/SKILL.md` + `tools/setup-emulator-host.ps1`（Hyper-V 检查/镜像/实例/启动/hdc）。
- 回放可复用帧：ArkTS 已同步 C# `WriteVisibleAt`/`RestorePrefix`（replay-frame 向量）。

## C# 基线演进同步（2026-11-18 评审修复轮）

会话期间用户 C# 工作树演进，黄金向量暴露并已同步：
- 图形：Circle/DashedCircle 改包围盒语义、EllipseWithFoci 改中点半径、Cone/Cylinder 重构、新增 ShapeKind 25–37（中心圆/虚线圆、含渐近线双曲线、正方形/三角形/直角三角形/菱形/平行四边形/梯形/四面体等），菜单 8 行 38 项。
- 笔尖：StrokeAttributes 新增 PenNib/NibRotation/NibTransform；bounds 采用最小 0.5 宽 + 笔尖椭圆半径；渲染轮廓应用 NibTransform。
- 设置：canvas 移除 hyperbolaAsymptoteOption、新增 penNib。
- 控制器：两步长方体改 GenerateCuboid（带符号深度）、双曲线渐近线按 kind 决定、识别圆改 CircleFromCenter、选区拖拽/克隆与双指钳制对齐当前 C#。
- 评审修复：跨文档撤销隔离（BindDocument）、课件墨迹持久化与 PDF/PixelMap 释放、手势/Pan/视图矩阵/翻页接线、触摸下移到画布层、修饰键大小写与焦点、是否窗 pending、截图建目录与提示、picker/IO 错误处理、画板加载坏文件不清空、画板资产缓存/释放/Transform、抽奖不重复修复、自动保存开关/prune 接线、浮条拖动与文字开关、resize 重绘。

## C# 基线演进同步（2026-11-19 缺口轮 5：笔锋/设置格式/存档契约）

用户 C# 工作树再次大幅演进（47 文件 +11k/−2.3k）。以 C# VectorGen 重生成黄金向量为 oracle 逐项同步：

- **笔锋（ink-taper）**：TaperKind 0..6（新增 平滑硬笔/柔和压感/自然/书法）；`SimulatedPressureTaper`（时间戳虚拟压感 EMA 速度 + 距离三次笔尾 + 64 槽环缓冲）与 `OneEuroFilter`（自适应低通、maxLag 2.5）1:1 移植；`TaperProfile` 类化（InkStroke 持有状态实例，换 kind 重建）；`StablePointCount` 稳定前缀 + `GeometryVersion`。新向量：taper-simulated（8）+ taper-stroke（5，滤波位置/稳定数全链路）。
- **Settings.json 契约**：新增可空 `canvas.highlighterWidth`（荧光笔独立宽度；null 时键省略 = C# JsonIgnore WhenWritingNull，解析缺失回落）；`inkStyle` 语义改为菜单索引（OriginalMenuCatalog.ToTaperKind 映射，7 项风格菜单）；`resetToRecommendation()`（重置设置推荐默认集，保留自动删除配置）+ 设置面板「重置」按钮。
- **OriginalMenuCatalog（core/settings 新文件）**：PEN_STYLES(7)/PEN_NIBS/ToTaperKind/ToPenStyle/ToEraserRadius（45×{.5,.8,1,1.25,1.8}）/HighlighterWidthMaximum(120) 等；向量 original-menu（2）。
- **笔/荧光笔宽度互换（G8）**：controller.ConfigurePenWidths + Tool setter 换 BaseWidth；entry 调色盘/设置接线（荧光笔宽独立存储、笔宽独立）。
- **存档契约（G13）**：.icink 与画板包 StrokeFile 增 `Nib`/`NibRotation` 持久化（双向，PascalCase，缺省 Round/0，越界回落）；画板包 Taper 校验扩到 0..6；schemas 同步。
- 调色盘 7 风格菜单 + 设置笔迹风格 7 项 + 橡皮半径统一走 toEraserRadius。
- 验证：**473 向量 / 50 文件全绿**（含重生成 oracle：taper-simulated/taper-stroke/settings-roundtrip 扩展/original-menu）；BUILD SUCCESSFUL；parity-check 见下轮收口。

### 剩余演进缺口（调研 docs/superpowers/research/harmony/16-baseline-evolution-gap.md，G1–G17）

**已补齐（2026-11-19 缺口轮 6，全部向量互认 + BUILD SUCCESSFUL）**：
- **G1 套索擦端到端**：core `LassoGeometry`（even/odd、点/段/笔画/框相交，6 向量）+ 控制器路径（视图 DIP、4096 抽稀、抬指闭合、恢复保叠放序 insertRange、"lasso erase" 单撤销单元、CancelLassoErase 公开命令）+ 浮条「套索」按钮 + 覆盖层（闭合虚线 + 35α 填充）。
- **G4 撤销语义对齐**：两步体（立方体/双曲线）完成后尊重 StickyShape；一般图形空结果不回笔；Invalidated 次数与 C# 逐路径核对一致（原头注三条"保留缺陷"中两条实为已对齐，注释已更正）。
- **G5 识别加固**：单笔直线优先（多笔拼接不进直线分支）；端点按住 ≥450ms/3px 门控；TryLine 最大垂偏（2.5px/跨度2%）+ 路径/直线比 ≥0.97（钩笔/回笔拒绝）+ <3px 静止抖动不计路径；识别结果统一宽度；ConfirmTap 屏幕空间落点收拢（触摸 4–6px/笔 1.5–3px，动画状态未建）。向量：jitter-hold/hook/back-over/multi-stroke。
- **G8 ConfigurePenWidths**（上一轮）+ **G10 画板页语义**：原子清空（墨迹+图片+视频单撤销单元）、衍至新页（选中克隆 + 新页单撤销）、翻页携带绘制状态（单控制器天然成立）；board-page-ops 3 向量。
- **G12 视频插入**：画板文件栏「视频」入口（选择→base64→Media 资产，渲染/进度已有）。
- **G16 布局纯逻辑**：FloatingToolbarLayout（候选位评分放置）+ BoardDockLayout（55DIP 三条两行自适应），6 向量。
- 模拟压感笔锋相对时间（_strokeStartTimes）同步到控制器书写路径。

**仍待补（下轮）**：G6 落点圆点动画状态（ConfirmTap 收拢已移植，动画计时/渲染未建）；G9 控制器级回放融合（独立回放窗可用）；G14 选区装饰细节（小对象边柄隐藏/旋转角度气泡）。

**已补齐（2026-11-19 缺口轮 12b，D8 纯逻辑；592 向量全绿 + BUILD SUCCESSFUL）**：
- **D8 PDF 纯逻辑四件**（core/pdf，zlib 外壳两端解耦——Node 用 node:zlib/真机用 @ohos.zlib）：
  - `PdfPageGeometry`：CropBox/UserUnit/旋转 ↔ 96DIP 双向映射（ToPdf/FromPdf 四旋向）；pdf-layout 10 向量（几何 4 + 页码范围 5 + 连续布局 1）。
  - `parsePdfPageRange`：1 基范围（中文逗号/去重/容量有界/中文错误）。
  - `PdfContinuousLayout`：页垂直居中排布 + NearestPage 二分/HitTest/VisiblePages。
  - `PdfInkCodec`：PDF 注释内无损二进制墨迹（ICK2 魔数/小端/float32 单精度往返/16 字节 GUID 域/LayerOrder int64/ShapeSource 文本限长/点数预算）——pdf-ink-codec 6 向量往返逐字段一致。
  - 向量运行器：`@kit.ArkTS` shim（parity/node/arkts-shim.mjs，TextEncoder.encodeInto/TextDecoder.decodeToString → Node）。
- **D8 写回落地（调研 18 结论驱动，API ≤12 全兼容）**：`PdfAnnotationWriter.ets`（platform/pdf）——每笔笔画一条标准 INK 批注（`InkAnnotationInfo.inkPoints` + `lineColor`，宽度/透明度借 `border.borderWidth`/`opacity`，荧光笔 alpha→opacity），世界→页 DIP（containPlacement=objectFit.Contain 同式）→PDF 用户空间（PdfPageGeometry 含旋转，getBox(BOX_CROP)/getRotation，无 UserUnit 按 1）；官方临时副本模式保存（文档不可同时读写）；`canIUse` 探测降级（PDF Kit 仅中国大陆→仅 sidecar）；`readPdfGeometries` 页几何枚举；课件工具面板「保存批注 PDF」入口 + CoursewareSession.pageDocument(i) 按页枚举。**保真限制（调研 §3/§4）**：单路径等宽、无 Nib/Taper/ShapeSource/LayerOrder——无损可编辑往返由课件会话 sidecar .icink（既有权威层）承担；外观流由 Kit 内部生成（渲染保真待真机）。
- **D8 不做项（调研 §4 结论）**：自定义批注键/流（/InkCanvasVersion//InkCanvasData 不可）、自建 PDF 写入器（相对 sidecar 无收益）、26.0.1 enablePen（Beta-only，生产不上）。

**已补齐（2026-11-19 缺口轮 12，D1 克隆层号 + D7 解析几何内核）**：
- **D1 SetCloneLayerOrder**：两处克隆路径（拖拽/双指手势）副本按源对象 z 序领取连续新层号（C# SetCloneLayerOrder 语义）。
- **D7 解析几何内核**（features/analytic，三个模块 + VectorGen AnalyticVectors 生成器——**C# 项目级 oracle 双向跑**）：
  - `MathExpression`：有界算术文法（4096 限长/512 节点/48 深度），归一（上标/NFKC/数学符号/π/θ）、隐式乘法、多字母变量积、pi/e、21 个数值函数、^ 右结合、错误消息带位置——14 向量。
  - `PlotCompiler`：直角（y=/x=/隐式 有无等号）/参数（x(t) 前缀）/极坐标（r 前缀）五类编译 + 变量域校验 + 中文错误——11 向量。
  - `PlotSampler`：640×480 屏幕格自适应曲线（128 区间/误差 0.55/黄金分割 0.382 校验/深 10）、Liang-Barsky 裁剪（**修复 enter/exit 跨四次 Clip 累积**——C# ref 语义）、隐函数 256×192 marching squares（二分求根 12 次/中心消歧）、1024 量化缝合；32000 点/220000 求值预算——6 向量（路径数/首末点/点数与 C# 逐位一致）。
- 剩余 backlog：D8 PDF 批注写回（需 PDF Kit 可行性调研，未开工）；D3/D4 的 SVG XML 校验/SVGZ 解压宿主侧。

**已补齐（2026-11-19 缺口轮 11，D1/D3/D5/D6）**：
- **D1 层排列系统**：ink `DrawingObject`/`LayerAction` + 纯算法（sortDrawingObjects/nextLayerOrder/arrangeObjects，layer-arrange 11 向量，含 ShapeSource 组保序）；控制器 InkLayers 段（DrawingObjects 懒重建/MarkLayerOrderDirty/NextLayerOrder/z 序统一命中 hitTestDrawingObject/CanArrange+ArrangeSelection 单撤销单元）；提交/识别/删除/套索/撤销重做均置脏 + 领层号；ISelectionElement 扩 layerOrder/isImageElement（BoardSelectionElement 实现）；选区工具条「顶层/上移/下移/底层」四按钮（CanArrange 门控）；画板元素按 DrawingObjects 层序渲染（墨迹与元素交错为单画布架构下的已记录简化）。
- **D5 名册+多次抽取**：`ClassroomRoster`（每行一名/CSV/TSV 姓名列/引号转义，roster 7 向量；GBK 回退解码宿主侧）+ LotteryEngine `draw(count)`（部分 Fisher-Yates 抽取内唯一 + 不重复池 + 历史封顶 20，lottery-draw 3 向量）+ entry「导入名单」按钮 + 抽奖走 draw。
- **D6 批量导页**：BoardDocument.insertPagesAfterCurrent（board-page-ops insertpages 探针）。
- **D3/D4（部分）**：`SvgSize`（viewBox/宽高归一 + CSS 单位换算 96DPI，svg-size 14 向量）；图片 Opacity 渲染（.opacity()）。XML 校验/命名空间修正/SVGZ 解压与 ImageSceneRenderer 矢量渲染为宿主侧/真机项；WebP/EXIF 由 image-kit 解码承担（真机核对）。
- 剩余 backlog：D7 解析几何内核、D8 PDF 写回（PDF Kit 可行性调研）；D1 的克隆拖拽层号（SetCloneLayerOrder）待接。

**已补齐（2026-11-19 缺口轮 9+10，第二波演进契约）**：
- oracle 漂移检测（重生成对比）发现第二波演进：.icink/画板包 LayerOrder+ShapeSource、Settings experimental 组（ScreenTracking 9 键 PascalCase）、图片 Opacity、视频 PosterData/NaturalWidth/NaturalHeight —— 全部双向持久化并向量互认（InkStroke.LayerOrder/ShapeSource、IcinkCodec、BoardPackCodec、BoardDocument、schemas、VectorGen 投影/字面量）。
- 增量调研：docs/superpowers/research/harmony/17-evolution-delta-2.md（D1–D8 分级）。**剩余 backlog**：D1 层排列系统（InkLayers/DrawingObject/LayerAction/z 序命中/ISelectionElement.LayerOrder+IImageSelectionElement）、D3/D4 SVG+WebP/EXIF/降采样导入策略、D5 ClassroomRoster+多次抽取历史、D6 InsertPagesAfterCurrent 批量导页、D7 解析几何绘图内核（纯逻辑可向量）、D8 PDF 批注写回（PDF Kit 可行性需调研）。插件运行时/ScreenTracking/SoftwareLauncher 等不构成 ArkTS 缺口（报告 §4）。

**已补齐（2026-11-19 缺口轮 8，G6/G9/G14 纯逻辑）——第一波 G1–G17 至此全部落地**：
- **G6 落点动画**：ink `InkDotAnimation`（120ms、smoothstep 缓动、向最终持久宽度过渡；构造 InkDotAnimation(dot, stroke)，dot-animation 3 向量）+ 控制器 DotAnimations/DotAnimationVersion/IsAnimatingDot/AdvanceDotAnimations/FinishDotAnimations + ConfirmTap 记录（多点/位移/宽度差条件）+ entry 常驻覆盖层画预览、16ms 帧步进自停。
- **G9 回放融合**：ink `InkPlayback`（display-only，InkReplaySession 克隆驱动，playback 2 向量）+ 控制器 Begin/Seek/EndPlayback（期间拒绝输入、切工具/换文档自动结束）+ entry 回放条驱动 SeekPlayback、画布只画可见前缀（源笔画与历史不动）。
- **G14（纯逻辑）**：core `visibleResizeHandles`（短/薄对象省略边中点柄，Transform*view 法向长度；selection-handles 5 向量）。渲染层接线（覆盖层用可见柄集 + 旋转箭头/角度气泡）为待办。

**已补齐（2026-11-19 缺口轮 7，G2/G3/G7/G11/G17；500 向量全绿 + BUILD SUCCESSFUL）**：
- **G2 元素选区桥**：ink 层 ISelectionElement/ElementSnapshot 契约 + 控制器 GetSelectableElements/SelectedElements 全路径接线（框选相交、点击优先命中、Ctrl/Shift 增减、删除 SetPresent+逆序 z 序恢复、克隆 Duplicate、双指手势实时 Transform、拖拽步进 delta、套索 IntersectsFrame、pruneSelection、选区 AABB 含元素框）；features BoardSelectionElement（z 序恢复/共享字节克隆/BoardElementMath 管线，6 向量）；entry 画板页注入图片+视频。features 新增 @inkcanvas/ink 依赖（分层允许）。
- **G3 选区命令 + 工具条**：RecolorSelection/ScaleSelectionWidth/RestoreSelectionWidth（ChangeSelectionAttributes 单撤销）/TransformSelectionUndoable（含元素）；选区工具条（placeToolbar 候选评分放置：克隆开关/9 色/B±/恢复/删除/取消，ArkTS 差异化紧凑形态）。
- **G7**：橡皮触摸期间指示环常显（不受 isShowCursor 限制；hover 语义 ArkTS 无对等，真机笔悬浮待核）。
- **G11**：插图/插视频记页内撤销单元。
- **G17**：RecommendedPollInterval 纯函数（200/1000ms，2 向量；PDF 课件无轮询宿主，仅对齐语义）。
- 撤销失效次数对齐：DeleteSelection 委托内部不失效（外层统一一次），controller.json selection-delete Inv 序列与 C# 逐位一致。

## 与 WinUI3 基线的已知差异（设计决定）


- 跨应用全屏批注不可行（`TYPE_FLOAT` 需 system_basic）→ 应用内批注 + 课件/图片/截图导入。
- PPT/WPS COM 不可行 → PDF 课件模式（逐页渲染 + 自绘墨迹）。
- 全局热键 → 应用内快捷键；静默截图 → 应用内快照；自更新 → 应用市场提示（Deferred）。
- 开机自启无三方 API → 唯一 not-ported。
- Segoe 图标不可分发 → Symbol 映射/自绘（Phase D 完成映射表）。

## 复刻的基线缺陷（互认优先，后续双端同步修复）

- 画板 Stroke 子格式缺 `HighlighterAlpha`（保存/加载后荧光笔透明度回落）。
- 仅视频无图片的页保存会丢媒体（§6 报告）。
- 窗口位置完全不落盘（WinUI3 基线即无；HarmonyOS 首版不新增，避免格式分叉）。
- PPT 轮询契约取 ShellController 实际值 200/900ms。
- 选区增粗/恢复笔宽只改 BaseWidth、不重算逐点 Width（无视觉反馈）。
- 三角识别被 InkController 忽略。

## 需真机验证清单

压感/倾斜/接触面积取值与单位；笔尾橡皮；悬停；`TouchType.Cancel` 稳定性；
Canvas `fillRule` 非零环绕与自交并集；双层 Canvas 提交闪烁；`StylusFrameBoost` 延迟与功耗；
PDF `renderPage` 分辨率/耗时/内存；AVPlayer 首帧；通知/代理提醒 APL；AppGallery 更新 API；
SymbolGlyph 图标名与起始版本；自由窗口/分屏断点；2in1 鼠标/键盘/焦点；深色与减少动效读取。
WPS 鸿蒙版包名设备覆盖（`bm dump -n cn.wps.mobileoffice.hap`/`cn.wps.office.hap`）与 viewData Want 打开/另存行为；.ppsx 是否自动放映；卸载 WPS 后 16000019 回退；
PiP 空 SURFACE + customUI 显示效果/自定义层触摸/后台计时保活与出声；弹簧曲线与 geometryTransition 实际效果；
AppGallery loadProduct/appdetail/checkAppUpdate 真机表现（需已上架+签名一致+前台+中国境内，模拟器必然失败）。

## 工具链

- Command Line Tools：`E:\EcoStudioCLI\command-line-tools`（26.0.1.290 beta，SDK 26.0.1 Beta1）。
- 构建缓存：`E:\harmony-cache`；会话环境：`tools/harmony-env.ps1`。
- 提交配置：省略 `compileSdkVersion`；`compatibleSdkVersion 6.1.1(24)`；`targetSdkVersion 26.0.0`；`modelVersion 26.0.0`（两处一致）。
- **上架限制**：Beta 工具链产物不可上架；发布前须换 26.0.0 Release 工具链重编译。
