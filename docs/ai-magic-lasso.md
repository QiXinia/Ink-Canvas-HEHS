# AI 魔法套索与助教浮窗（HarmonyOS / ArkTS）

> 目标：在套索/选择工具中集成 AI 入口，框选墨迹或屏幕局部后弹出 Fluent 风格助教浮窗，
> 以 SSE 流式返回公式转写、自动求解、图形释义与多轮问答，并一键贴回画板。
> 本功能仅在鸿蒙线（`harmony/`）实现，C#/WinUI3 侧未回移；manifest 状态 `adapted`。

## 1. 目录与职责

| 文件 | 职责 |
|---|---|
| `harmony/core/src/main/ets/assistant/AssistantTypes.ts` | 状态/模式/事件枚举，请求与事件模型（纯类型） |
| `.../AssistantMachine.ts` | 显式状态机 `idle→capturing→recognizing→streaming→done/error`、首 token 超时、重试、追问 |
| `.../LassoGeometry.ts` | 自由曲线闭合判定、多边形点包含、笔画命中比例、选区有效性（最小 32×32） |
| `.../AnchorLayout.ts` | 浮窗吸附/翻转/边界钳制/缩放跟随（≥12vp 安全边距） |
| `.../LatexLite.ts` | LaTeX 子集转纯文本（frac/sqrt/上下标/积分/希腊字母）、半成公式兜底 |
| `.../MarkdownLite.ts` | Markdown 渐进解析（标题/列表/代码块/表格/引用），未闭合块标记 |
| `.../PasteBackModel.ts` | 贴回对象构造（右侧 16vp）、视口钳制、插入/撤销/重做语义 |
| `.../SseParser.ts` | SSE 增量解析；兼容 `event:` 自定义事件与 OpenAI `choices[].delta.content` |
| `harmony/platform/src/main/ets/assistant/AiConfig.ets` | 端点/模型/超时配置（环境变量优先） |
| `.../AiClient.ets` | `requestInStream` + `dataReceive` 流式客户端、首 token 超时、Abort；无端点时本地 mock 流 |
| `.../AssistantCapture.ets` | 选区截图（`componentSnapshot` region）+ 长边 1600 压缩 + base64 |
| `harmony/controls/src/main/ets/MagicLassoEntry.ets` | 「AI 魔法识别」pill 入口（Q / 长按进入套索） |
| `.../AssistantCard.ets` | 360vp 浮窗：状态点、模式 Tab、缩略图、流式区、操作区、追问输入、拖动 |
| `.../StreamMessage.ets` | Markdown/公式渐进渲染 + 流式光标 |
| `harmony/entry/src/main/ets/pages/Index.ets` | 套索采集路由（入口 / Q / Ctrl 框选）、浮窗挂载与跟随、贴回与 undo、Q/Esc 快捷键 |

## 2. 数据流

```
选择工具 → (Q/点击入口) 套索 armed
  → onTouch 记录自由路径（不进入 InkController 的矩形选择）
  → 抬手：闭合判定 + 多边形命中笔画（复用 SelectionGeometry/InkDocument 命中模型）
  → 截图 base64 + 笔画矢量（视图坐标，含压力/时序）
  → AssistantRequest（6.1 契约）→ AiClient
  → SSE 事件 → SseParser → AssistantMachine → StreamMessage 逐 token 渲染
  → 完成：贴回画板 Images（ContentType=application/vnd.inkcanvas.ai）
  → BoardElementHistory 压入单个撤销单元（Ctrl+Z 整体撤回）
```

## 3. 依赖与环境变量

新增第三方依赖：**无**。使用系统 Kit：`@kit.NetworkKit`（http）、`@kit.ArkUI`（componentSnapshot/display）、`@kit.ImageKit`（PixelMap/ImagePacker）、`@kit.ArkTS`（TextDecoder/Base64Helper/process）、`@kit.BasicServicesKit`（pasteboard）。

| 变量 | 默认 | 说明 |
|---|---|---|
| `AI_ENDPOINT` | 空 | OpenAI 兼容 SSE 端点；为空时使用内置 mock 流（无需网络） |
| `AI_MODEL` | `inkcanvas-assistant` | 请求体 `model` |
| `AI_TIMEOUT_MS` | `15000` | 首 token 超时（也用于连接超时） |
| `MOCK_AI_PORT` | `8787` | 仅 mock 服务使用的端口 |

真机联调时，`AI_ENDPOINT` 不能写 `127.0.0.1`（指设备自身），应写宿主局域网地址，例如
`http://192.168.1.10:8787/v1/chat/completions`。

## 4. 启动 mock 服务

```bash
node tools/mock-ai-sse.mjs
# 另一终端验证：
curl -N -X POST http://127.0.0.1:8787/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"mode":"solve"}'
```

mock 覆盖：`formula` / `solve` / `shape` / `chat` / `auto`，以及 `mode=error`（错误态）。

## 5. 自测用例清单

| # | 用例 | 手段 | 状态 |
|---|---|---|---|
| 1 | 公式识别求解（token + formula + step 顺序） | `assistant-machine` / `assistant-sse` 向量 | ✅ 自动通过 |
| 2 | 几何图形释义（shape_annotation 事件） | `assistant-sse` 向量 + mock `mode=shape` | ✅ 事件层通过；实机叠加未验证 |
| 3 | 流式中断/重试（Abort 保留已生成内容） | `AiClient.Abort` + `stopAssistant` 代码路径 | ⚠ 需真机/网络联调 |
| 4 | 空白/过小选区拦截（<32×32、未闭合、未命中） | `assistant-lasso` 向量（3 例） | ✅ 自动通过 |
| 5 | 浮窗边界翻转（下→上、右→左、钳制） | `assistant-anchor` 向量（4 例） | ✅ 自动通过 |
| 6 | 白板缩放/平移跟随（以选区中心为锚点） | `followAnchor` + `updateAssistantCardPosition` | ✅ 纯逻辑通过；实机帧率未验证 |
| 7 | 贴回后撤销（单撤销单元、位置钳制） | `assistant-paste` 向量（3 例） | ✅ 纯逻辑通过；实机 Ctrl+Z 未验证 |
| 8 | 暗色主题（ThemeTokens 驱动，强调色不硬编码） | `AssistantCard`/`StreamMessage` 使用 `themeTokens(dark)` | ⚠ 需真机观感确认 |

补充：`assistant-latex`（4 例）与 `assistant-markdown`（2 例）覆盖半成公式兜底与渐进块解析。

## 6. 验证结果与边界

已执行：

- `hvigorw assembleHap --mode module -p product=default -p buildMode=debug --no-daemon`：**BUILD SUCCESSFUL**（未签名 debug HAP）。
- `node --experimental-transform-types --experimental-loader ./parity/ts-resolve-loader.mjs parity/run-vectors.mjs`：**480 passed / 0 failed（54 文件）**。
- `node tools/parity-check.mjs --final`：**OK（85 锚点）**。

未验证（如实记录）：

- 未在真机/模拟器启动应用；2 秒首字、Acrylic 实机效果、焦点陷阱与 Tab 序、200% 缩放、暗色观感均未实机核对。
- 未连接真实 AI 服务；`requestInStream` 的流式分块节奏与真实网关行为未联调。
- 选区截图按 `densityPixels` 换算 vp→px；不同窗口缩放/分屏下的裁切精度未实机核对。
- 贴回为画板 `Images` 资产（JSON 元数据 + 文本卡片渲染），非上游对象模型；拖动/保存/加载复用既有图片链路。
- `annotations` 目前仅在回答中提示「已叠加标注」，未把标注点绘制到画板图层。