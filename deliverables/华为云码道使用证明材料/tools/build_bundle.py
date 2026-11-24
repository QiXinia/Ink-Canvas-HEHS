"""Create a reviewable evidence bundle from supplied screenshots and log excerpts."""

from pathlib import Path
import hashlib
import html
import json
import re
import shutil

import cv2
from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[1]
SOURCE = Path(r"C:\Users\Administrator\Downloads\码道鸿蒙")
MANUAL = ROOT.parent / "product-manual"
EVENTS = json.loads((MANUAL / "source-assets/codearts/events.json").read_text(encoding="utf-8"))
ASSETS = MANUAL / "public/assets"
PROOFS = ASSETS / "codearts"
HARMONY = ASSETS / "harmony"
FONT = ImageFont.truetype(r"C:\Windows\Fonts\msyh.ttc", 18)

SHOTS = [
    ("CA-01", "2437aa9e7699a1b2d551bc34dcccd123.jpg", "平台能力分析与方案选择", []),
    ("CA-02", "34def3c00aa4ccdb5301bd11a1ecc14a.jpg", "Windows到鸿蒙的工具栏设计重构", [(100, 921, 287, 953)]),
    ("CA-03", "4a9ad629da21d5fdc043b0dba500967d.jpg", "面板跟随折叠与滚动的Prompt及结果", [(195, 728, 448, 766)]),
    ("CA-04", "4f4041be283ae838868a487bb274baf7.jpg", "拖动与图标排错的Prompt及工具调用", []),
    ("CA-05", "660a8dab9c97bc1a7144dd4fd0b42698.jpg", "玻璃材质与多形态布局实现记录", []),
    ("CA-06", "72a694133b72bb9e60bc8ee60255dcab.png", "OCR真实性与空结果边界修复", []),
    ("CA-07", "799629912dc92b9e98441915a44d2fda.png", "拖动工具栏误写的根因与验证", []),
    ("CA-08", "cd2832474af14763084ed61d356e61b0.jpg", "AI与响应式能力的验收状态", []),
]
CLIPS = [
    ("HM-01", "0984ebd789dfd138701d7b727f0078c5.mp4", "harmony-writing", "书写、笔选项与工具栏折叠", 9.5),
    ("HM-02", "416ed5b81957803f99e8407805c0b4b6.mp4", "harmony-tools", "化学结构与白板切换", 37),
    ("HM-03", "789c667888e8482b02de98df401d6f60.mp4", "harmony-pdf", "生物传感器讲义阅读与批注", 41),
]
CASES = [
    {
        "id": "R-01", "folder": "02-代码生成与重构记录", "title": "公式识别与搜题结果结构改造",
        "prompt": "我要求你优化你的AI公式识别以及智能搜题功能",
        "promptSource": "日志中的用户输入，2026-11-24T07:47:04.237",
        "process": "读取公式与结果卡实现，调整公式候选加权、题型排序和质量提示，修改 AiAssistant.ts、AiStudio.ets 与 Index.ets。",
        "result": "可见助手回复报告新增已知／所求、候选公式、易错提醒和自适应卡片。工具输出保留“25 passed, 0 failed”，编译验证另有成功记录。",
        "boundary": "设备手写 OCR 返回空的情形仍存在。逻辑断言与结果卡显示的验证范围分别记录，不能据此声称任意手写公式都能准确识别。",
        "events": [775, 781, 794, 845, 1113], "shots": ["CA-06", "CA-08"],
    },
    {
        "id": "R-02", "folder": "02-代码生成与重构记录", "title": "工具栏折叠与设置面板跟随重构",
        "prompt": "我需要你在鸿蒙端实现这个功能，即我的工具栏可以始终悬停在任何界面/应用上，进行批注 同时点击设置后，出现的弹窗与工具栏不跟随，我要求你将其修改为跟随工具栏移动，并且折叠，利用滚轮操作 我希望你的折叠效果如图一所示，折叠之后仍可选择功能，只是可选功能减少",
        "promptSource": "CA-03 用户输入区逐字转录，截图中的附件标题省略",
        "process": "以同一拖动位移驱动面板，增加 settingsPanelOffsetX/Y 边界钳制，使用 CompactCard 呈现折叠后的常用工具，设置分组改为手风琴。",
        "result": "日志保留 CompactCard 分支和面板偏移函数的具体 edit 记录。可见回复报告面板跟随、滚动、折叠和安装验证；HM-01 可观察展开／折叠及书写。",
        "boundary": "跨应用悬浮涉及系统权限。记录提供系统多任务悬浮窗路径，普通应用自动置顶与穿透能力仍受平台限制。",
        "events": [867, 869, 916, 918, 924], "shots": ["CA-01", "CA-02", "CA-03", "CA-05"],
    },
    {
        "id": "R-03", "folder": "02-代码生成与重构记录", "title": "化学结构绘制与墨迹共存",
        "prompt": "现在有一个问题，我在使用这个功能时，你禁用了我的批注功能，请你将两者兼容，同时请你继续对齐chemdraw的功能，使之更加美观",
        "promptSource": "日志用户输入，2026-11-24T10:59:47.425",
        "process": "由独立面板演进为内嵌化学层，整理原子、键、芳香环与撤销状态。切换笔、荧光笔、橡皮和选择时，使化学层让出触摸输入。",
        "result": "Index.ets 的 edit 记录增加 chemInputActive=false 的工具门控。HM-02 中可见化学键线与原浮动工具栏、白板切换。",
        "boundary": "录屏展示化学结构基础交互。完整 ChemDraw 功能兼容、结构持久化与全部导出能力未由这些材料证明。",
        "events": [1301, 1428, 1433, 1454, 1482], "shots": ["CA-04"],
    },
    {
        "id": "D-01", "folder": "03-排错与验证记录", "title": "ArkTS编译失败到重新构建成功",
        "prompt": "现在请你解决第3个bug 1.在拖动工具栏时，第二次拖动工具栏的移动与触控位置有便宜2，请你解决2.工具栏的图形请放大一些，严格按照windows端的样式修改，同时严禁出现两个一模一样的图案3.请你制作这个功能：适配鸿蒙平板横竖屏、折叠屏与自由多窗（分屏一边看教材、一边用 InkCanvas 记板书）：打造可折叠悬浮胶囊工具条 要求，在切屏到另一个页面时，能够保留工具栏在其他应用之上，除非关闭应用程序",
        "promptSource": "CA-04 用户输入区转录，保留原输入用词与编号",
        "process": "读取 FloatingBarCatalog.ts 与 IconGlyph.ets，生成 glyph-dump.js 输出图标码点，检查图标重复，继续编译和视觉微调。",
        "result": "同一批日志保留 ArkTSCompilerError／BUILD FAILED 与随后“BUILD SUCCESSFUL in 9 s 22 ms”“73 tasks”的工具输出。",
        "boundary": "该记录证明编译迭代与成功构建，不将编译成功等同于全部 UI 或硬件行为验收。部分原始日志自身含有 ****，摘录保持原状。",
        "events": [374, 383, 388], "shots": ["CA-04", "CA-02"],
    },
    {
        "id": "D-02", "folder": "03-排错与验证记录", "title": "浮层命中与工具栏拖动误写排查",
        "prompt": "截图记录了对浮动工具栏拖动与画布输入冲突的排查，原始提问未在该截图中完整呈现。",
        "promptSource": "CA-07 的根因、修复方式与验证表；相关工具面板排查见日志",
        "process": "CA-07 指出透明命中容器使同一触摸落到下层画布。修复按实际工具栏屏幕区域拦截 Down，并保持同一指针 Move／Up 的一致处理。另一日志定位 Scroll 与无显式宽度 Column 的循环依赖。",
        "result": "截图给出 onAreaChange、handleTouch、6 vp 容差及拖动后继续书写、清空等复测说明。日志中的设置钳制代码提供另一组可核对的输入／布局修复证据。",
        "boundary": "本材料使用截图中的排错报告与日志字段，未在本机重放这些鸿蒙触摸测试。多设备和不同缩放的手势仍需扩展验证。",
        "events": [918, 1045], "shots": ["CA-07", "CA-03"],
    },
    {
        "id": "D-03", "folder": "03-排错与验证记录", "title": "OCR不可用与手写空结果的明确反馈",
        "prompt": "我要求你优化你的AI公式识别以及智能搜题功能",
        "promptSource": "日志中的用户输入，2026-11-24T07:47:04.237",
        "process": "移除按选区大小产生固定结果的演示路径，改为画布快照裁剪与 textRecognition；对识别文本的公式行和题型进行处理，空结果反馈引擎状态或墨迹统计。",
        "result": "CA-06 明确模拟器缺少 OCR 能力。后续日志报告设备上手写笔画返回空时显示墨迹统计与提示，逻辑测试有 25 项通过记录。",
        "boundary": "端侧印刷体 OCR、手写公式识别与远程模型推理是不同环节。材料未证明真机手写公式识别的完整成功链路。",
        "events": [843, 845, 1113], "shots": ["CA-06", "CA-08"],
    },
]


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def clean_event(index):
    event = dict(EVENTS[index])
    event.pop("session", None)
    event.pop("message", None)
    event.pop("id", None)
    event["excerptLimit"] = "每个字段最多12000字符，超出部分以标记说明"
    for key in ["text", "output"]:
        value = re.sub(r"\x1b\[[0-9;]*m", "", event.get(key, ""))
        if key in event:
            event[key] = value[:12000] + ("\n[后文略]" if len(value) > 12000 else "")
    inputs = {}
    for key, value in event.get("input", {}).items():
        if key == "description":
            value = value.split('","timeout"')[0].split('","output"')[0]
        inputs[key] = value[:12000] + ("\n[后文略]" if len(value) > 12000 else "")
    event["input"] = inputs
    return event


def main():
    for path in [PROOFS, HARMONY, ROOT / "01-AI交互与Prompt截图", ROOT / "04-鸿蒙运行素材"]:
        path.mkdir(parents=True, exist_ok=True)
    sources = []
    shots = []
    for ident, filename, title, boxes in SHOTS:
        image = Image.open(SOURCE / filename).convert("RGB")
        draw = ImageDraw.Draw(image)
        for box in boxes:
            draw.rectangle(box, fill="#303030")
            draw.text((box[0] + 4, box[1] + 2), "设备标识已隐去", font=FONT, fill="white")
        target = ROOT / "01-AI交互与Prompt截图" / f"{ident}-{title}.jpg"
        image.save(target, quality=96)
        shutil.copyfile(target, PROOFS / f"{ident}.jpg")
        shots.append({"id": ident, "title": title, "file": target.relative_to(ROOT).as_posix()})
        sources.append({"id": ident, "source": filename, "sourceSha256": digest(SOURCE / filename), "file": target.relative_to(ROOT).as_posix(), "sha256": digest(target), "redactedRectangles": boxes})
    crops = [("CA-03", (65, 416, 967, 599), "prompt-follow"), ("CA-04", (89, 270, 969, 493), "prompt-debug"), ("CA-07", (10, 151, 968, 678), "debug-hit-test"), ("CA-02", (12, 142, 963, 665), "refactor-toolbar"), ("CA-06", (11, 87, 968, 815), "ocr-boundary")]
    for ident, box, name in crops:
        image = Image.open(PROOFS / f"{ident}.jpg")
        image.crop(box).save(PROOFS / f"{name}.jpg", quality=96)
    clips = []
    for ident, filename, name, title, seconds in CLIPS:
        target = ROOT / "04-鸿蒙运行素材" / f"{ident}-{title}.mp4"
        shutil.copyfile(SOURCE / filename, target)
        shutil.copyfile(target, ASSETS / "media" / f"{name}.mp4")
        capture = cv2.VideoCapture(str(target))
        duration = capture.get(cv2.CAP_PROP_FRAME_COUNT) / capture.get(cv2.CAP_PROP_FPS)
        capture.set(cv2.CAP_PROP_POS_MSEC, seconds * 1000)
        ok, frame = capture.read()
        if not ok:
            raise RuntimeError(f"Cannot read {filename}")
        poster = ROOT / "04-鸿蒙运行素材" / f"{ident}-画面.jpg"
        Image.fromarray(cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)).save(poster, quality=95)
        shutil.copyfile(poster, HARMONY / f"{name}.jpg")
        capture.release()
        clips.append({"id": ident, "title": title, "file": target.relative_to(ROOT).as_posix(), "poster": poster.relative_to(ROOT).as_posix(), "duration": round(duration)})
        sources.append({"id": ident, "source": filename, "sourceSha256": digest(SOURCE / filename), "file": target.relative_to(ROOT).as_posix(), "sha256": digest(target), "posterSeconds": seconds})
    for case in CASES:
        folder = ROOT / case["folder"]
        folder.mkdir(parents=True, exist_ok=True)
        selected = [clean_event(index) for index in case["events"]]
        filename = f'{case["id"]}-{case["title"]}'
        lines = [f'# {case["id"]} {case["title"]}', "", "## 原始需求或提问", "", case["prompt"], "", case["promptSource"], "", "## 代码生成、重构或排错过程", "", case["process"], "", "## 结果与验证记录", "", case["result"], "", "## 验证范围", "", case["boundary"], "", "## 对应截图", ""]
        for shot_id in case["shots"]:
            shot = next(shot for shot in shots if shot["id"] == shot_id)
            lines.append(f'- [{shot_id} {shot["title"]}](../{shot["file"]})')
        lines.extend(["", "## 可见日志摘录", "", "以下来自日志的用户可见回复或工具调用。原日志含有的 `****` 保持原样。部分不规范 JSON 按字段截取，均附原文件名与行号，不作为完整会话导出。", ""])
        for event in selected:
            lines.extend([f'### {event["logTime"]} · {event.get("tool", event["kind"])}', "", f'原日志 `{event["source"]}` 第 {event["line"]} 行。', ""])
            if event.get("text"):
                lines.extend([event["text"], ""])
            for key in ["filePath", "description", "oldString", "newString", "content"]:
                value = event.get("input", {}).get(key)
                if value:
                    excerpt = value[:5000]
                    lines.extend([f'字段 `{key}`', "", "```text", excerpt, "```", ""])
            if event.get("output"):
                lines.extend(["工具输出节选", "", "```text", event["output"][:3500], "```", ""])
        (folder / f"{filename}.md").write_text("\n".join(lines), encoding="utf-8")
        (folder / f"{filename}.jsonl").write_text("\n".join(json.dumps(event, ensure_ascii=False) for event in selected), encoding="utf-8")
        case["file"] = (folder / f"{filename}.md").relative_to(ROOT).as_posix()
    index = {"title": "华为云码道使用证明材料", "date": "2026-11-24", "screenshots": shots, "cases": CASES, "harmonyClips": clips, "sources": sources}
    (ROOT / "05-来源与索引/材料索引.json").write_text(json.dumps(index, ensure_ascii=False, indent=2), encoding="utf-8")
    rows = [f'| {item["id"]} | {item["title"]} | [{item["file"]}]({item["file"]}) |' for item in shots]
    case_rows = [f'- [{case["id"]} {case["title"]}]({case["file"]})' for case in CASES]
    README = ["# 华为云码道使用证明材料", "", "本文件夹依据用户提供的码道截图、全量日志包与鸿蒙操作录屏整理，日期为 2026-11-24。截图保留码道界面标识、原始 Prompt、代码修改和工具执行轨迹。", "", "## 快速查看", "", "打开 `index.html` 浏览截图与视频，或打开各案例 Markdown 查看代码与排错过程。`华为云码道使用证明-摘要.pdf` 摘自产品说明书中的码道三页。", "", "## 目录", "", "- `01-AI交互与Prompt截图` 保存 8 张用户提供截图的整理副本。", "- `02-代码生成与重构记录` 包含公式识别、工具栏与面板、化学绘制三个案例。", "- `03-排错与验证记录` 包含编译迭代、触摸命中、OCR 边界三个案例。", "- `04-鸿蒙运行素材` 包含三段原始录屏副本及按时间截取的预览帧。", "- `05-来源与索引` 保存源文件名、SHA-256、日志行号与分类索引。", "", "## 截图索引", "", "| 编号 | 内容 | 文件 |", "| --- | --- | --- |", *rows, "", "## 过程记录", "", *case_rows, "", "## 材料处理说明", "", "- 原始日志包共 130 个文件，解压后约 919 MB。采用流式读取，节选与本项目相关的用户可见回复、Prompt 和工具调用，按源文件与行号回溯。日志时间保留原格式，未推断时区。", "- 截图中的设备标识已遮盖，文本摘录中的本机用户目录、设备号、链接及网络地址按规则隐去。原日志包自身的 `****` 掩码保持原状。", "- 全量原包保留在用户提供目录，索引记录其哈希。本材料只包含评审所需的选段，未复制会话内部推理、记忆元数据和原始网络请求。", "- 部分日志不是规范 JSON，摘录通过可见字段恢复，并注明 `partialRecord`。截图和原日志行号用于交叉核对，未补写缺失内容。", "- 鸿蒙录屏是用户提供的运行材料，显示书写、折叠、PDF 批注、化学结构和白板流程。配套对话报告 MatePad／HarmonyOS 7.0.0.111 安装与测试。本次未重新连接设备测量延迟、帧率、功耗或全面兼容性。", "- OCR 手写公式全链路、自由多窗与分屏覆盖，以及普通应用跨应用悬浮权限仍有边界，各案例中明确保留。", ""]
    (ROOT / "README.md").write_text("\n".join(README), encoding="utf-8")
    gallery = "".join(f'<article><h3>{shot["id"]} {html.escape(shot["title"])}</h3><a href="{shot["file"]}"><img loading="lazy" src="{shot["file"]}" alt="{html.escape(shot["title"])}"></a></article>' for shot in shots)
    case_cards = []
    for case in CASES:
        excerpts = []
        for event_index in case["events"]:
            event = clean_event(event_index)
            fields = []
            for key in ["filePath", "oldString", "newString", "content"]:
                value = event.get("input", {}).get(key)
                if value:
                    fields.append(key + "\n" + value[:2200])
            if event.get("output"):
                fields.append("output\n" + event["output"][:1000])
            if not fields:
                continue
            source_label = f'{event["logTime"]} · {event["source"]} · 第 {event["line"]} 行'
            excerpts.append(f'<p class="source">{html.escape(source_label)}</p><pre>{html.escape(chr(10).join(fields))}</pre>')
        details = f'<details><summary>展开原始 Prompt 与日志摘录</summary><blockquote>{html.escape(case["prompt"])}</blockquote><p class="source">{html.escape(case["promptSource"])}</p>{"".join(excerpts)}</details>'
        case_cards.append(f'<article class="case"><small>{case["id"]}</small><h3>{html.escape(case["title"])}</h3><p>{html.escape(case["process"])}</p><p>{html.escape(case["boundary"])}</p>{details}<a href="{case["file"]}">打开完整过程记录 ↗</a></article>')
    case_cards = "".join(case_cards)
    videos = "".join(f'<article><h3>{clip["id"]} {clip["title"]}</h3><video controls preload="none" poster="{clip["poster"]}" src="{clip["file"]}"></video><p>{clip["duration"]} 秒 · 用户提供的鸿蒙操作录屏</p></article>' for clip in clips)
    page = f'''<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>华为云码道使用证明材料</title><style>
    *{{box-sizing:border-box}}body{{margin:0;background:#edf2f8;color:#19334e;font:15px/1.8 "Microsoft YaHei",sans-serif}}header{{padding:50px max(24px,calc((100vw - 1240px)/2));background:#153f6d;color:white}}h1{{font-size:34px;margin:0 0 14px}}header p{{max-width:950px;color:#d6e5f6}}main{{max-width:1240px;margin:30px auto;padding:0 20px 60px}}h2{{margin:40px 0 20px}}.grid{{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:22px}}article{{background:white;border:1px solid #d8e3ef;border-radius:14px;padding:20px;min-width:0}}h3{{font-size:18px;margin:0 0 14px}}img{{display:block;width:100%;height:480px;object-fit:contain;background:#181818;border-radius:8px}}a{{color:#0874c8;text-decoration:none}}video{{width:100%;border-radius:8px}}small{{color:#688099}}.case p{{color:#64788e}}.note{{padding:22px;background:white;border-left:4px solid #0874c8;border-radius:8px}}@media(max-width:750px){{.grid{{grid-template-columns:1fr}}h1{{font-size:27px}}}}
    pre{{max-height:420px;overflow:auto;white-space:pre-wrap;overflow-wrap:anywhere;background:#edf3f9;padding:14px;border-radius:8px;font:13px/1.7 Consolas,"Microsoft YaHei",monospace}}blockquote{{margin:14px 0;padding:12px;border-left:3px solid #0874c8;background:#edf3f9}}summary{{cursor:pointer;color:#0874c8;padding:10px 0}}.source{{font-size:12px;color:#8395a8;overflow-wrap:anywhere}}
    </style></head><body><header><h1>华为云码道使用证明材料</h1><p>核心功能开发的 AI 交互、Prompt、代码生成与重构、排错和验证记录。依据用户提供的截图、日志与鸿蒙运行素材整理。</p></header><main><p class="note">共 8 张交互截图、6 份过程案例和 3 段运行录屏。点击截图可查看完整分辨率。源文件哈希、原日志行号和材料处理说明见 README 与来源索引。</p><h2>AI 交互与 Prompt 截图</h2><section class="grid">{gallery}</section><h2>代码生成、重构与排错</h2><section class="grid">{case_cards}</section><h2>鸿蒙运行素材</h2><section class="grid">{videos}</section><h2>来源与验证范围</h2><p class="note">设备标识已遮盖，原包保留在用户提供目录。录屏和对话记录说明所示操作已运行，本次未重新连接设备测量性能。OCR、分屏、跨应用权限等限制详见案例。<a href="README.md">阅读完整索引 ↗</a></p></main></body></html>'''
    (ROOT / "index.html").write_text(page, encoding="utf-8")
    print(json.dumps({"screenshots": len(shots), "cases": len(CASES), "videos": len(clips)}, ensure_ascii=False))


if __name__ == "__main__":
    main()
