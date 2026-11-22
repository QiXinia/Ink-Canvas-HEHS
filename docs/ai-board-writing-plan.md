# AI 板书整理与智能书写：实现方案与差距清单

- 分支：`新增AI板书整理与智能书写功能`
- 基线：`96956f2`（含板中板、AI 修复、AI 网关）
- 技术栈：C#/.NET 10 + WinUI 3 + Win2D；不改 ArkTS/Harmony

## 1. 现状审查结论

| 能力 | 现状 | 差距 |
|---|---|---|
| 可编辑文本对象 | **不存在**；只有 `AiPasteMetadata` 文本元数据 + PNG 卡片 | 需新增可编辑文本对象/编辑入口 |
| 公式对象 | **不存在**；AI 卡片把 LaTeX 当纯文本画入 PNG；项目只有 MathML（`MathMarkup`/`MathFormulaView`），无 LaTeX 渲染器 | 需 LaTeX→MathML 转换与排版渲染，保存 LaTeX 源 |
| 对象组 | **不存在**；只有形状笔画 `ShapeSource.GroupId` | 需“一次撤销”的多对象原子写入 |
| 写入链路 | `PasteAssistantAsync` 把内容渲染成 **1 张 PNG**，`AddImage` 一步撤销 | 需多对象、可编辑源、按页/板路由 |
| 目标路由 | `AssistantTarget` 已支持 Overlay/MainBoard/SideBoard + 页 + 会话校验 | 可复用；需补按 `PageIndex` 寻址 |
| 自动布局 | 仅居中/适配视口/锚点翻转；无内容避障 | 需内容包围盒 + 空白区规划器 |
| 保存 | v4 封闭 schema（Strokes/Images/Media）；`BoardImageAsset.Metadata` 已持久化 | 复用图片+元数据可零格式变更；新增独立对象需可选字段 |
| 时光机 | 只录笔画；`AiStep` 是预留位，未接线；回放只返回笔画 | 需 AI 事件 + 确定性 PNG 回放 + 板标识 |

## 2. 总体方案（增量，不复制白板）

### 2.1 对象模型

- 沿用 `BoardImageAsset`（PNG）+ 扩展 `AiPasteMetadata` 表达 AI 板书对象：
  - `Kind`: Text / WordList / Poem / Formula / BoardPlan
  - `Latex`（公式源）、`Text`、`Lines`、`Style`（字号/行距/对齐/列数）、`Instruction`、`CreatedAt`、`Source`
- 渲染结果 PNG 走现有 Blob（main/side），元数据随 `Metadata` 持久化 → **零格式变更、旧文件兼容**。
- 双击编辑复用现有 `EditAiCardAsync`：改文本/LaTeX → 重新渲染 PNG → `UpdateImage`（一次撤销）。

### 2.2 渲染

- 文本/词语/古诗：新增 Win2D `BoardTextCardRenderer`（`CanvasTextLayout` 真实测量、换行、对齐、列、2x DPI），替代 `PasteCardRenderer` 的 7.2 平均字宽估算。
- 公式：`LatexToMathML`（覆盖分式/上下标/根号/矩阵/方程组/常用符号）→ `MathFormulaView/MathFormulaPreview` → `RenderTargetBitmap` 缓存 PNG；失败回退文本卡片。LaTeX 源始终保留。
- 高 DPI：按 2x 渲染，上限 8192px，与子板贴回一致。

### 2.3 写入与撤销

- 新增 `BoardPlanWriter`：把结构化 `BoardPlan` 转成多个 `BoardImageAsset`（公式/文本/词语/古诗各一卡）。
- 新增 `BoardService.AddImagesAtomic(images, actionName)`：一个 `DelegateAction` 完成多对象插入/撤销。
- 写入前 `AiTargetStillCurrent(writeBack:true)`；写入按 `_aiTarget.Kind + PageIndex` 解析目标板与页（不足时补 `AddImagesToPage`）。

### 2.4 指令与预览

- `AssistantWindow` 新增“整理板书”“AI 书写”按钮；复用追问输入框接收自然语言。
- 请求 `Purpose="board_write"` + `Instruction` + `TargetPageIndex`；新增 SSE 事件 `board_plan`（结构化 ops）。
- 预览：浮窗内展示内容分块与目标位置摘要；“整理板书”显示原文/结果对照。
- 确认后写入；支持“重新生成/局部重写/复制到另一板/删除本次 AI 内容”。

### 2.5 自动布局

- 新增 `BoardContentLayoutPlanner`：输入当前页内容包围盒（笔画+图片+媒体）与待插入卡片尺寸，输出不重叠的 `Left/Top`；空间不足时缩小、换位或建议新页。
- 新增 `BoardService.GetContentBounds()` 供规划器与贴回复用。

### 2.6 保存与时光机

- 保存：复用图片+`Metadata`，v4 不变；旧文件无 AI 对象正常打开。
- 时光机：`AiStep` 事件补 `BoardId/ObjectIds/Kind/Action/RenderBlobRef`；回放只用已确认 PNG，不重新请求模型；`BuildObjectFrame` 与时光机/导出增加对象层。
- PPT/WPS：可结合当前页文本上下文生成，但写入目标必须确认。

## 3. 数据结构

```jsonc
// board_plan SSE data
{
  "type": "board_plan",
  "schema": 1,
  "intent": "organize|write",
  "target": { "board": "main|side", "pageIndex": 0 },
  "blocks": [
    { "id": "b1", "kind": "formula", "latex": "\\frac{1}{2}x^2", "text": "",
      "rect": { "x": 0.55, "y": 0.1, "w": 0.4, "h": 0.15 },
      "style": { "fontSize": 24, "align": "left", "columns": 1 } },
    { "id": "b2", "kind": "poem", "title": "静夜思", "author": "唐·李白",
      "lines": ["床前明月光", "疑是地上霜"], "rect": { ... } }
  ],
  "notes": ["第 2 行识别置信度较低"]
}
```

`AiPasteMetadata` 扩展字段：`Kind`、`Schema`、`Latex`、`Text`、`Lines`、`StyleJson`、`Instruction`、`CreatedAt`、`Notes`。

`AiStep` Payload：`board`、`pageIndex`、`objects[{id,kind,rect,latex,text,lines,blobRef}]`、`action`。

## 4. 实施阶段与验收

| 阶段 | 内容 | 验收 |
|---|---|---|
| 1 | 统一写入链路：`board_plan` 解析 → 预览 → 确认 → 多对象原子写入 → 一次撤销 | 构建 0 错；单测与实机（mock 剧本）通过 |
| 2 | 四类内容排版：公式（LaTeX→MathML）、文本、词语列表、古诗；双击编辑 | 公式/古诗结构正确；写入后可编辑/缩放/撤销 |
| 3 | 整理板书：选区/当前页识别 → 原文/结果对照 → 部分采用 | 原文保留；低置信度提示；部分采用 |
| 4 | 自动布局避障、主/子板路由、保存重开、时光机确定性回放 | 不覆盖原内容；跨板不串；回放一致 |

## 5. 风险

1. 项目无 LaTeX 渲染器；`MathMarkup` 仅受限表达式，需自研 `LatexToMathML` 覆盖常用结构。
2. `RenderTargetBitmap` 在非前台窗口可能挂起；公式渲染需在预览阶段（浮窗存在）完成并缓存 PNG。
3. 多对象原子撤销需一次 `DelegateAction`，不能逐对象 `AddImage`。
4. 时光机 `BuildFrame` 返回类型仅笔画；新增对象回放应加方法而非改签名，避免破坏现有回放/导出。
5. 旧版本客户端打开含 AI 对象的文件后另存可能丢失（未知字段被忽略），需在文档提示。
## 6. 实施结果（2026-11-22）

### 6.1 交付范围

- **统一写入链路**：`board_plan` 协议（SSE）→ 浮窗预览 → 确认 → 多对象 `AddImagesAtomic` 一次撤销。
- **入口**：浮窗「整理板书」「AI 书写」按钮 + 追问框自然语言；`--ai-endpoint/--ai-model` 启动参数。
- **四类内容**：公式（`LatexToMathML` + `MathFormulaPreview`/RTB）、文本、词语列表（多列）、古诗（标题/作者/正文分行）。
- **整理**：原文/整理两栏对照、置信度与笔误警示、块勾选部分采用、替换原板书一次原子撤销。
- **连续修改**：字号/列数/过滤/移动目标/行距，本地确定性变换，不重新请求模型。
- **自动布局**：内容包围盒避障、逐级缩放（1.0→0.7）、空间不足建议新页。
- **目标路由**：`AssistantTarget` + 写回校验（同板+同页+同页对象+同会话）；主/子板均实机验证。
- **保存/时光机**：复用图片+Metadata（v4 不变）；`AiStep`（BoardId/action/objects/PNG）录制；`BuildObjectFrame` 确定性回放（PNG 原样，不请求模型）。
- **编辑**：主展板与板中板双击卡片 → 共享 `AiCardEditor` → 重渲染 → 一次撤销。

### 6.2 数据结构

`AiPasteMetadata` 扩展：`Kind=BoardPlan`、`Schema`、`Latex`、`Text`、`Lines`、`StyleJson`、`Instruction`、`CreatedAt`、`Notes`、`BlockKind`、`Title`、`Author`。

`AiStep` Payload：`objectId/action/schema/pageIndex/boardId/objects[{id,kind,left,top,width,height,layerOrder,latex,text,lines,renderPngBase64(≤400KB),renderMissing,notes}]`。

### 6.3 排版规则

- 公式：LaTeX→MathML（分式/根号/上下标/矩阵/方程组/希腊字母/常用符号）；RTB 2x；失败回退文本并记录原因。
- 文本：`CanvasTextLayout` 真实测量；标题/正文/行距/对齐/列；超宽超高按 1.0→0.7 缩放，仍超出截断并标注。
- 古诗：标题居中、作者/朝代小字、正文逐行居中；横排。
- 词语列表：词条+释义多列（1/2/3 列），列间距稳定。

### 6.4 验证证据

- 构建：**0 警告 0 错误**。
- 测试：**1651 项全通过**（Core 1119 / Ai 168 / P0 47 / Classroom 314 / LegacyImport 3）。
- `--aiboardcheck` 实机：**18/18 PASS**（2 optional），exit=0：
  - 4 类卡片写入、一次撤销/重做、替换原板书原子撤销；
  - v4 保存重开元数据/字节一致；子板 Blob Scope=side；
  - 时光机 AiStep 录制、checkpoint 增量、会话重载、PNG 一致、回放无网络依赖；
  - 路由映射 MainBoard→main / SideBoard→side / Overlay→main，跨页/跨板写回拒绝。
- 实机 UI：
  - 整理预览显示原文/结果/置信度 0.78/笔误警示；取消第 4 块后仅写入 3 卡；
  - 全部采用写入 4 卡（公式排版、文本、词语、静夜思），不重叠；
  - 连续修改（字号放大→两列→只保留公式）后仅 1 张公式卡，网关无新请求；
  - Ctrl+Z 一次撤销全部卡片；
  - 板中板 AI 书写写入子板（日志 board=side），子板截图 4 卡正常；
  - 子板双击公式卡 → 编辑 LaTeX `+2`→`+5` → 重渲染成功。

### 6.6 2026-11-22 增补（评估缺口）

- **讲义导出**：新增 `BoardPageRenderer` 与“板书 第 N 页”源；PDF 页数 = PPT 页 + 有内容白板页；渲染失败不再静默（`Success=false`），共享设备不可用时回退软件渲染；测试注入确定性 PNG，断言追加页/字节/尺寸/PDF 页数。
- **空间不足**：规划器状态机 `Placed/NeedsShrink/NeedsMove/NeedsNewPage`；弹窗“缩小排版写入/写入新的一页/取消”；实机在子板第二次写入触发并成功写入第 2 页（`newPage=True`），撤销后卡片消失、**共 2 页保留**。
- **空页 AI 书写**：按意图区分——`write` 允许空页，`organize` 仍要求内容；修复空页被“无内容”拦截。
- **板中板选择**：新增「选择」工具（复用 InkController），选中显示框与 8 控制点；真实鼠标拖动/缩放各产生一次 `transform selection`（日志 undo=2/3），删除一次 `delete`；撤销回原位。
- **拖动事件修复**：双击编辑监听改为只观察、不设 Handled，避免吞掉选择拖动；自动化需产生真实 `PointerMoved`（相对 mouse_event）。

### 6.7 差距与未验证项（不得视为已达成）

1. **真实 AI 端点**：本期按用户确认使用本地网关 mock；真实模型指令解析与 JSON 稳定性未验证。
2. **PPT 讲义视觉**：结构/页数/字节一致已测试，真实 PPT 截图 + 板书页混排的视觉未实机检查。
3. **高 DPI/多屏/暗色高对比度**：仅单屏 125%；公式/卡片清晰度与对比度未验证。
4. **人工文件对话框保存重开**：由 BoardService/诊断验证，未走对话框。
5. **WPS/PPT 生成流程联动**：未联动。
6. **大 PNG/长会话**：400KB 阈值、时光机画面帧率、会话体积未压测。
7. **子板时光机 UI**：`BuildObjectFrame(boardId:1)` 有逻辑测试，但时光机窗口默认叠加 `boardId=0`，子板录制/回放 UI 未接线。
8. **触摸/手写笔**：子板选择拖动/缩放仅鼠标验证。