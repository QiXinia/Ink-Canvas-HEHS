# AI 板书整理与文字智能书写（非生图）实现方案

- 分支：`新增AI板书整理与智能书写功能`
- 关键约束：AI 文字/公式必须是**白板原生可编辑对象**，禁止渲染成 PNG/JPG/SVG/位图后贴入

## 1. 现状与差距

| 能力 | 现状 | 结论 |
|---|---|---|
| 白板文本对象 | **不存在**；文字仅作为 `BoardImageAsset` 的元数据，实际渲染为 PNG 图片 | 违反“非生图”，必须新增原生文本对象 |
| 公式对象 | **不存在**；LaTeX→MathML 仅用于 PNG 渲染 | 必须新增可编辑公式对象（保存 LaTeX 源，矢量绘制） |
| AI 写入 | `BoardPlanWriter` 生成 PNG 卡片 + Metadata | 必须改为直接创建文本/公式对象，不再生成图片 |
| 选择/变换 | `ISelectionElement` 契约 + InkController 选择系统（图片/笔画可用） | 新对象实现该契约即可复用移动/缩放/撤销 |
| 保存 | BoardFile v4 封闭 schema（Strokes/Images/Media） | 新增可选 `TextObjects`/`FormulaObjects` 字段，旧文件兼容 |
| 时光机 | AiStep 记录 PNG Base64 | 改为记录结构化对象（type/content/latex/style/geometry），回放矢量重绘 |
| 导出/讲义 | BoardPageRenderer 绘制笔画/图片 | 增加文本/公式矢量绘制 |
| 分组 | 无持久化对象组 | 新增 groupId；一次写入同组，可整体选择/移动/取消分组 |

## 2. 对象模型（新增）

```csharp
public sealed class BoardTextObject : ISelectionElement {
    string Id; string Type = "text";      // text | wordList | poem | summary
    string Content;                        // 正文（可含换行）
    string Title; string Author; string Dynasty;
    string FontFamily; double FontSize; string Color;
    double LineHeight; string Align;       // left|center|right
    double Left, Top, Width, Height; int LayerOrder;
    string GroupId; string Source = "ai"; string Instruction; long CreatedAt;
    // 选择契约：Bounds/Frame/Transform/Duplicate/Capture/Restore...
}
public sealed class BoardFormulaObject : ISelectionElement {
    string Id; string Type = "formula";
    string Latex;                          // 源，必存
    string FontFamily; double FontSize; string Color;
    double Left, Top, Width, Height; int LayerOrder;
    string GroupId; string Source = "ai"; string Instruction; long CreatedAt;
}
```

- 文本渲染：`CanvasTextLayout`（Win2D 矢量文字），直接画到画布，**不是图片**。
- 公式渲染：`LatexToMathML` → MathML AST → 自绘布局（分式/上下标/根号/矩阵/方程组/希腊字母）→ `CanvasDrawingSession` 画文字与线条；LaTeX 源始终保留。
- 编辑：双击打开 ContentDialog，修改 Content/LaTeX/字号/字体/对齐 → 重绘 → `UpdateTextObject/UpdateFormulaObject`（一次撤销）。
- 复制/换行/字号/字体：原生对象属性，直接修改。

## 3. 写入与撤销

- `BoardService` 新增：
  - `AddTextObjectsAtomic/AddFormulaObjectsAtomic`（或统一 `AddAiObjectsAtomic`）：一次 `DelegateAction`。
  - `UpdateTextObject/UpdateFormulaObject`：一次撤销。
  - `RemoveAiObjects`：一次撤销。
- 写入前校验 `boardId/pageId/sessionId/generationId`；过期丢弃。
- 一次指令的多对象共享 `GroupId`，写入后默认全选该组；支持整体移动/缩放、取消分组（清除 GroupId）。

## 4. 保存与兼容

- `BoardPageFile` 增加可空 `List<BoardTextObjectFile> TextObjects`、`List<BoardFormulaObjectFile> FormulaObjects`；`Version` 保持 4（旧版读到未知字段忽略，不报错）。
- 子板同样写入（Scope 不涉及，文本/公式为 JSON 内联，无二进制）。
- 旧文件缺字段 → 空；不生成空白遮挡。
- 导出/讲义/时光机均使用结构化数据重渲染，不以文字截图作为数据源。

## 5. AI 链路调整

- `BoardPlanWriter` 不再调用 `BoardTextCardRenderer/BoardFormulaRenderer/PasteCardRenderer`，改为创建 `BoardTextObject/BoardFormulaObject`。
- `AiPasteMetadata` 仅保留给历史图片卡片兼容；新写入不再产生图片对象。
- `AiStep` Payload 改为结构化对象（不含 PNG Base64）。

## 6. 分阶段

| 阶段 | 内容 | 验收 |
|---|---|---|
| 1 | 对象模型 + 矢量渲染 + 编辑 + 保存 + 原子撤销 + 选择/变换 | 单测 + 实机“写一句总结”为可编辑文字 |
| 2 | 四类内容（句子/词语/古诗/公式）排版与编辑 | 词语可改单条、古诗分行、公式可改 LaTeX |
| 3 | 整理对照/部分采用/连续修改（复用现有） | 原文保留、部分采用、一次撤销 |
| 4 | 主/子板路由、时光机结构化回放、导出/讲义 | 保存重开/导出/回放一致、无生图 |

## 7. 风险

1. Win2D 中文字体度量与换行：需 `CanvasTextLayout` 实测，避免与卡片视觉差异过大。
2. 公式自绘排版工作量：先覆盖常用结构，复杂矩阵/方程组标记上限并保留 LaTeX。
3. 旧图片卡片兼容：历史文件仍可显示/编辑（元数据），但新生成路径禁止新增图片对象。
4. `ISelectionElement` 实现需完整（Capture/Restore/Transform/Duplicate），否则选择/撤销异常。
5. 保存 schema 扩展需同步校验/读取/子板/缩略图/导出，遗漏会导致数据丢失。