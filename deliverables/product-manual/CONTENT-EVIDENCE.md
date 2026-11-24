# 产品报告内容依据

本文件配套 28 页 A4 产品说明书，记录重构贡献、功能覆盖和验证范围，便于后续补充设备材料。整理日期为 2026-11-24。

## 开源基础与项目贡献

项目以 Ink Canvas 与 Ink Canvas Artistry 为基础，沿用 GPL-3.0，并保留上游来源与署名。报告第 2 页具体说明技术迁移、代码分层、墨迹引擎、窗口交互、存档和模块化扩展。当前页面截图来自实际 WinUI 3 窗口。

| 报告主题                          | 文档或实现依据                                                                                                                                      | 图文页  |
| --------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------- | ------- |
| WPF 到现代 Windows 桌面技术的迁移 | `docs/PORTING-NOTES.md`、`docs/PROJECT-DESCRIPTION.md`、`src/InkCanvas.App`                                                                         | 2、4、7 |
| 领域与界面分层                    | `docs/EXTENSIONS.md`、`src/InkCanvas.Core`、`InkCanvas.Platform`、`InkCanvas.Ink`、`InkCanvas.Controls`、`InkCanvas.Features`                       | 4–5     |
| 模块契约与扩展运行时              | `src/InkCanvas.Host.Contracts/DocumentWorkspace.cs`、`src/InkCanvas.Extensions/ModuleRuntime.cs`、`InkCanvas.Extensibility`、`InkCanvas.PluginHost` | 5、18   |
| 分层墨迹与脏区更新                | `src/InkCanvas.Ink/Rendering/InkSceneRenderer.cs`、`OverlayInkPresenter.cs`、`docs/PERFORMANCE.md`                                                  | 6       |
| 流式 ZIP 媒体存档                 | `src/InkCanvas.Features/Board/BoardService.Persistence.cs`                                                                                          | 5、17   |
| WinUI 3 与触摸组件                | `src/InkCanvas.Controls/Controls/PointerSlider.cs`、共用窗口框架与主题资源、`docs/PORTING-NOTES.md`                                                 | 7–8     |

## 功能与展示覆盖

| 功能                               | 依据                                                                    | 截图或演示                                |
| ---------------------------------- | ----------------------------------------------------------------------- | ----------------------------------------- |
| 自然笔锋、真实压力、倾斜与水墨     | `docs/PERFORMANCE.md`、墨迹模型和笔刷实现                               | 8 页笔刷样本与选项、23 页椭圆推导视频     |
| 自定义颜色、滑动调节与面积擦       | `CHANGELOG.md` 3.11.0–3.14.0                                            | 8 页画笔与橡皮面板                        |
| 分页、每页纸型和页面管理           | `src/InkCanvas.Features/Board/BoardService.cs`、`BoardPageNavigator.cs` | 9 页白板、21 页索引                       |
| 对象、克隆、顶点、填充、线型与图层 | `src/InkCanvas.Ink/InkController.Shapes.cs`、选区与图层实现             | 10 页图形总览、23 页图形视频              |
| 图片、矢量图与白板视频             | BoardImageAsset、BoardMediaAsset 及媒体导入实现                         | 10 页功能表、21 页索引                    |
| 当前画布墨迹回放                   | InkPlayback 与 InkReplaySession                                         | 10 页功能表、21 页索引                    |
| PPT、多屏与无焦点激活              | `docs/PORTING-NOTES.md`、PPT 服务与桌面覆盖层                           | 7、11 页、用户提供的放映截图              |
| 实验性屏幕跟随                     | `docs/SCREEN-TRACKING.md`、屏幕跟随模块                                 | 11 页状态与边界说明                       |
| PDF 阅读、搜索、跨页批注与矢量写回 | `docs/PDF.md`、3.12.0 与 3.14.0 变更记录                                | 12 页、23 页 PDF 视频                     |
| 数学构造、CAS、键盘与导出          | `docs/ANALYTIC-GEOMETRY.md`                                             | 13–14 页三维截面与键盘截图，23 页几何视频 |
| AI、协议、附件与 Agent 工具        | `docs/AI-ASSISTANT.md`、模块工具注册表                                  | 15 页真实 DeepSeek 回复、18 页模块说明    |
| 计时、秒表与点名软冷却             | 课堂工具源码及设置                                                      | 16 页三个实际窗口状态                     |
| 保存、文稿库、命名建议、续接与同步 | `docs/BOARD-LIBRARY.md`、`docs/CLOUD-SYNC.md`、3.14.0 变更记录          | 17 页文稿库、22 页可编辑案例              |
| 插件下载、设置注册与更新           | `docs/EXTENSIONS.md`、更新源与扩展管理实现                              | 18 页原生模块管理截图                     |
| MSI、便携包与运行时脚本            | `docs/PROJECT-DESCRIPTION.md`、发行脚本                                 | 24 页部署流程                             |

21 页提供跨章节的功能索引。镜像更新源以通用策略描述。公开材料未包含部署实例地址、账号、密钥或对象存储凭据。

## 验证范围

- 性能表使用 `docs/PERFORMANCE.md` 中 2026-11-20 的单机离屏渲染基准，保留分辨率、笔画量、帧数与 GPU 读回条件。实体触控端到端延迟与高刷帧率需另测。
- PDF 文档包含部分历史描述，页面数与当前 UI 说明优先核对当前代码及变更记录。报告未引用过时的 99 页硬上限。
- 几何模块的通用命令兼容、任意构造证明、隐式采样精度及复杂展开图仍有边界，14 页明确说明。
- AI 界面截图来自视频工程保存的真实 DeepSeek / deepseek-flash 响应，原样加载到原生窗口重新取景，工具成功新建第 2 页。完整生成等待过程未在镜头中呈现。真实服务需用户配置，四类 API 协议、模型和工具能力以连接实际支持为准。
- Windows 片段通过真实原生窗口与业务控制器采集并合成，展示业务流程，实体手指与笔的输入性能需另测。

## 复杂课堂案例与核对

第 22 页采用用户提供的全国一卷 PDF 第 4 页第 18 题，覆盖第 1 问和第 2 问（i）。题面年份以用户文件记载为准。案例连接原题批注、白板推导、真实模型建议和可编辑存档。

- 由左焦点 F(−1, 0) 与离心率 1/2，得 a = 2、b² = 3，椭圆为 x²/4 + y²/3 = 1。
- 设 y = k(x + 1)，联立得到 (3 + 4k²)x² + 8k²x + 4k² − 12 = 0。
- 由韦达及面积条件得到 x₁ + 2x₂ + 3 = 0，正斜率解为 k = √5/2。
- 独立代入 P = (1/2, 3√5/4)、Q = (−7/4, −3√5/8)、R = −P，检查三点在椭圆上、P/Q 在直线上、Q 位于第三象限，且面积比为 3。结果写入 `out/qa/case-math-validation.json`。
- 425 条手绘中心线路径经实际墨迹控制器回放。可编辑白板和批注 PDF 位于 `public/assets/cases`，源文件哈希保存在 `source-manifest.json`。
- PDF 采集证据记录翻页前后均为 22 条批注。47 种图形全览和立方体截面使用新版原生素材。片段只展示采集到的操作，不据此推断硬件性能。
- 模型原样响应与工具结果使用视频工程已完成请求的证据，未重新发送网络请求。

原生素材与制作说明见 `../product-demo/README.md`、`production/rebuild/design.md` 和 `production/rebuild/revision/independent-review.md`。原型窗口采集、原生输入回放及重新取景的区别已在说明书及素材来源中标注。

## 鸿蒙与码道补充材料

原有架构依据 `docs/harmony-porting-notes.md`、`docs/ai-magic-lasso.md`、`parity` 与 `harmony` 代码。2026-11-24 补入用户提供的三段鸿蒙操作录屏、八张码道截图和全量日志包的相关选段，详见 `docs/HARMONY-EVIDENCE-20261124.md`。

- 第 20 页可播放 HM-01 至 HM-03，显示书写、笔选项、折叠、PDF 阅读批注、化学结构和白板流程。
- 第 25–28 页使用 CA-02、CA-03、CA-04、CA-06、CA-07 的完整截图或注明的局部，覆盖原始 Prompt、界面重构、代码生成和排错。
- 独立证明文件夹包含 13 张交互截图、6 份案例、逐案 JSONL、3 段原始录屏副本及来源哈希。
- R-01、R-02、R-03 分别记录公式识别、面板折叠和化学与书写共存；D-01 至 D-03 分别记录编译迭代、命中排错及 OCR 空结果。
- 编译日志保留 BUILD FAILED、ArkTSCompilerError 与后续 BUILD SUCCESSFUL。逻辑输出含 25 passed, 0 failed，原有 \*\*\*\* 掩码保持原状。
- 用户提供的码道对话报告 MatePad／HarmonyOS 7.0.0.111 安装与测试。本次未重新连接设备，按录屏可见操作和日志范围说明，不推断端到端延迟、帧率、功耗或全面兼容性。
- OCR 手写识别空文本、多窗／分屏和跨应用权限仍有边界。PDF 写回保真、完整几何内核、化学持久化和发布签名需进一步验证。
- 早期移植文档记录原阶段，后续设备素材与迭代情况以本补充说明为准。

## 后续补充位

- 码道使用材料已补齐到第 25–28 页，后续可增加更多任务的完整设备回归记录。
- 鸿蒙已补入操作录屏和码道安装报告，目标设备的压力、延迟、多窗与性能测量可继续补充。
- 教室真实红外屏、混合 DPI 双屏及不同压感笔的对照测量待补。

## 新标志及成员证明补充

应用标志由 Images/Brand/Logo.Source.svg 生成，浅色笔画 #0067C0、深色 #60CDFF。WindowFrame 随实际主题更新标题栏与原生窗口 ICO。

第 28 页使用成员艾尼萨尔提供的 CA-S01 至 CA-S05 截图及过程文档，补充函数解释、校验代码生成和网关／脚本排错。作者报告的测量和测试以原文保留，本次未取得其引用提交及代码树，未将结果归为本机重新验收。
