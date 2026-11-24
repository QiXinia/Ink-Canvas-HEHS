# 素材与内容来源

## 本次内容修订

说明书为 28 页。包含重构贡献、依赖分层、流式存档、渲染管线、WinUI 3 与触摸体验、插件、鸿蒙适配、功能索引和解析几何课堂案例，逐项依据见 `CONTENT-EVIDENCE.md`。文案保留上游来源与 GPL-3.0 声明。

鸿蒙部分依据 `docs/harmony-porting-notes.md`、`docs/ai-magic-lasso.md`、`parity` 与 `harmony` 代码。架构图由 React／CSS 排版，标为代码架构示意。另补入 2026-11-24 用户提供的鸿蒙录屏与码道记录，设备操作可见范围和专项性能验证分别说明。

## 本地操作视频与静态帧

六段新版 Windows 演示来自相邻工程 `../product-demo/public/rebuild`，复制至 `public/assets/media`。对应最终截图与两个可编辑案例文件位于 `public/assets/cases`。`scripts/import-case-assets.py` 记录原文件路径、SHA-256、片长及截图裁切，详情保存在 `cases/source-manifest.json`。

| 片段             | 演示功能                                                   | 报告使用        |
| ---------------- | ---------------------------------------------------------- | --------------- |
| advanced-writing | 全国一卷第 18 题的手绘椭圆、韦达与面积关系，425 条编排路径 | 1、8、22、23 页 |
| gaokao-pdf       | 原题焦点与面积条件圈记、手写方程、翻页返回后恢复 22 条批注 | 12、23 页       |
| shape-catalog    | 47 种图形，按真实轮廓范围独立排布，保留构造对象            | 10、23 页       |
| geometry3d       | 立方体、平面与截线，改变观察角度                           | 10、13、23 页   |
| assistant-real   | 真实 DeepSeek 响应与 board_page 新建页面                   | 15、23 页       |
| classroom        | 倒计时、暂停与多人点名                                     | 23 页           |

椭圆案例取自用户提供的《高考数学全国一卷》PDF 第 4 页第 18 题，年份按原文件记载，展示范围为第 1 问与第 2 问（i）。题面版权与出处归原作者，文件由用户提供用于本项目展示。板书路径由演示工程编排，通过原生墨迹控制器回放，保存为可编辑 `.icink`。题目原页与批注副本均未改写题干。

`ellipse-board-detail.png` 从完整板书裁切导航区域，`assistant-response-detail.png` 裁去底部空输入区，几何不同视角截图来自同一片段的 15% 与 65% 进度。透明画面按原主题铺设中性底色，未重绘控件。

真实 AI 记录在演示工程 `production/rebuild/revision/frames/real-ai-evidence.json` 与 `real-ai-response.json`，模型为 deepseek-flash，工具成功增加第 2 页。展示时将已保存的真实响应原样加载到原生窗口重新取景。生成等待时间未完整展示，未据此报告推理速度。实体笔输入延迟和真机帧率需另测。

秒表截图 `stopwatch.png` 来自已有本机诊断 `source-assets/native/countdown/timer-stopwatch-laps-1.png`，透明像素合成到浅色中性背景后用于印刷。

完整产品视频 A／B／C 位于相邻演示工程 `out/rebuild`，各 2 分 54 秒。其鸿蒙段为明确标注的占位素材，具体替换位置见该工程 README。报告中的鸿蒙章节已加入用户新提供的运行录屏，完整视频的占位替换由演示工程另行更新。

## 文字依据

产品内容基于仓库的 `docs/PROJECT-DESCRIPTION.md`、`docs/PERFORMANCE.md`、`docs/PDF.md`、`docs/ANALYTIC-GEOMETRY.md`、`docs/AI-ASSISTANT.md`、`docs/BOARD-LIBRARY.md`、`docs/CLOUD-SYNC.md` 和 v3.14.0 更新记录。

说明书介绍已实现的能力与使用场景。云同步明确需要自行配置服务，AI 说明注明模型连接条件，码道章节依据用户提供的截图与日志补齐。

## 用户提供的素材

用户提供 `inkcanvasa.zip`，其中图片解压到 `source-assets`。说明书使用以下图片并裁切为适合版面的局部。

| 原文件                  | 发布素材             | 用途               |
| ----------------------- | -------------------- | ------------------ |
| `20261123233834560.png` | `board-writing.png`  | 用户中文板书原素材 |
| `20261123234153143.png` | `board-solids.png`   | 立体几何板书       |
| `20261123233142794.png` | `ppt-annotation.png` | PPT 放映批注场景   |

源截图内的课堂材料与手写内容保持原样。网页与 PDF 中对图像等比排版，部分页面使用裁切显示。

## 本机 WinUI 3 截图

使用 v3.14.0 可运行版本的原生界面与现有诊断入口，在隔离的设置目录中生成示例内容。生成文件保存在 `source-assets/native`。

| 诊断入口              | 本机截图                                                 | 用途                             |
| --------------------- | -------------------------------------------------------- | -------------------------------- |
| `--geometrycheck`     | `geometry/workbench-2d.png`、`geometry/workbench-3d.png` | 二维函数、三维截线与原生公式输入 |
| `--librarycheck`      | `library/library-1-1420.png`                             | 文稿库导航与预览                 |
| `--countdowncheck`    | `countdown/timer-ready-1.png`                            | 倒计时窗口与预设控件             |
| `--windowresizecheck` | `lottery/lottery-1.png`                                  | 点名抽选窗口                     |

画笔与橡皮选项使用本轮项目验收产生的 `penoptions/pen.png`、`eraseroptions/eraser-1.png`，笔迹样本使用对应的 `stroke-*.png`。

PDF、AI 与空间探究页面使用本节列出的新版原生采集截图。第 18 页的模块管理截图保留此前原生设置素材 `06-modules-50.jpg`，该画面展示插件设置卡片。

通过 RenderTargetBitmap 获取的透明窗口内容按相应主题铺设中性背景，以用于印刷。界面截图未用虚构的网页控件替代。

## 字体与图标

| 资源                        | 来源与许可                                                                                                   |
| --------------------------- | ------------------------------------------------------------------------------------------------------------ |
| Source Sans 3               | Google Fonts 的 `ofl/sourcesans3`，SIL Open Font License。https://fonts.google.com/specimen/Source+Sans+3    |
| Noto Sans SC                | Google Fonts 的 `ofl/notosanssc`，SIL Open Font License。https://fonts.google.com/noto/specimen/Noto+Sans+SC |
| Fluent System Icons Regular | 项目随附的微软开源图标字体，MIT。https://github.com/microsoft/fluentui-system-icons                          |
| Ink Canvas 标志             | 仓库 `Images/Ink Canvas Artistry.png`                                                                        |

字体许可文件随网页资源保留。所有字体在本地加载并随浏览器打印嵌入 PDF。

## 用户补充的鸿蒙与码道材料

来源为用户提供的“码道鸿蒙”目录，含 8 张 CodeArts 交互截图、3 段鸿蒙操作录屏和 `码道记录-全量.zip`。原包含 130 个文件，未将全量日志直接复制进网页或证明材料。独立文件夹 `../华为云码道使用证明材料` 保存来源 SHA-256、截图编号、原日志文件名与行号、选段处理说明。

| 编号           | 原文件                               | 用途                                 |
| -------------- | ------------------------------------ | ------------------------------------ |
| HM-01          | 0984ebd789dfd138701d7b727f0078c5.mp4 | 书写、笔选项与折叠，第 20 页         |
| HM-02          | 416ed5b81957803f99e8407805c0b4b6.mp4 | 化学结构与白板，第 20 页             |
| HM-03          | 789c667888e8482b02de98df401d6f60.mp4 | 生物传感器 PDF 阅读批注，第 20 页    |
| CA-01 至 CA-08 | 8 张用户截图，原文件名映射见材料索引 | 码道 Prompt、重构与排错，第 25–28 页 |

视频原尺寸均为 1096×720，时长分别为 约 21、84、58 秒，按原文件复制。预览帧取自 9.5、37、41 秒。截图局部均标明 CA 编号，完整整理副本可在证明文件夹查看。CA-02 与 CA-03 的设备标识已遮盖，其余对话内容保留。

日志选段只含用户可见回复、Prompt 和工具调用，附来源字段。原日志含有的 `****` 保持不变，部分非标准 JSON 标注 partialRecord。证明材料中的代码片段来自日志记录，未将推测代码补入。用户目录、设备号、链接与网络地址按规则处理。

配套对话报告 MatePad／HarmonyOS 7.0.0.111 安装与测试。录屏可观察所示操作，本次未重新连接设备验证硬件压力、延迟、帧率、多窗覆盖及 PDF 保存保真。OCR 手写空结果等边界继续保留。

## 新标志与成员补充

新标志源自用户提供的《白板logo.svg》，按浅深主题生成透明轮廓与蓝色笔画，应用标题栏、EXE／安装图标、说明书封面与页眉统一更新。说明书第 7 页展示两种配色。原采集截图反映采集时的界面，品牌展示元素使用新版资源。

《码道使用证明-艾尼萨尔-20261124.zip》补入五张函数解释、pageIndex 校验、poly 参数、bat 编码及项目全景截图，两份作者原文档原样保留。第 28 页使用注明编号的截图局部。测试、效率数字为作者自述，原文引用提交本次未取得。详细来源与说明见证明材料中的 06-艾尼萨尔补充。
