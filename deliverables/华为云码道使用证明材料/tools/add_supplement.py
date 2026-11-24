"""Add the member-supplied evidence while preserving its original documents."""

from pathlib import Path
import hashlib
import html
import json
import zipfile

from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
SOURCE = Path(r"C:\Users\Administrator\Downloads\码道使用证明-艾尼萨尔-20261124.zip")
SUPPLEMENT = ROOT / "06-艾尼萨尔补充"
MANUAL = ROOT.parent / "product-manual" / "public" / "assets" / "codearts"
SHOTS = [
    ("CA-S01", "02-回答-解释函数.png", "流式网关函数解释"),
    ("CA-S02", "03-提问-生成校验函数.png", "pageIndex连续性校验代码生成"),
    ("CA-S03", "05-提问-报错排查.png", "poly宽度参数排错"),
    ("CA-S04", "06-提问-bat乱码.png", "bat编码与执行排错"),
    ("CA-S05", "07-项目全景.png", "契约与网关项目全景"),
]


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    SUPPLEMENT.mkdir(parents=True, exist_ok=True)
    entries = []
    with zipfile.ZipFile(SOURCE) as package:
        for item in package.infolist():
            if item.is_dir():
                continue
            name = item.filename
            if not item.flag_bits & 0x800:
                try:
                    name = name.encode("cp437").decode("gbk")
                except UnicodeError:
                    pass
            target = (SUPPLEMENT / name).resolve()
            if not target.is_relative_to(SUPPLEMENT.resolve()):
                raise ValueError("Archive path leaves evidence directory")
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(package.read(item))
            entries.append({"name": name, "bytes": target.stat().st_size, "sha256": sha(target)})
    shots = []
    for ident, name, title in SHOTS:
        path = SUPPLEMENT / "截图" / name
        image = Image.open(path)
        image.save(MANUAL / f"{ident}.png")
        # A labelled crop makes the native screenshot legible on A4.
        image.crop((635, 80, 1536, 770)).save(MANUAL / f"{ident}-detail.png")
        shots.append({"id": ident, "title": title, "file": path.relative_to(ROOT).as_posix(), "sha256": sha(path)})
    note = """# 艾尼萨尔补充材料说明

本组由用户提供的《码道使用证明-艾尼萨尔-20261124.zip》导入，保留 5 张原截图及两份作者原文档，作者署名为艾尼萨尔，记录周期为 2026-11-19 至 24 日。

| 编号 | 内容 | 可核对范围 |
| --- | --- | --- |
| CA-S01 | call_upstream_stream 函数解释 | 可见回复引用 server.py，说明上游请求、OpenAI 流式帧、错误与结束状态 |
| CA-S02 | pageIndex 连续性校验 | 可见接线代码与校验器、边界用例和样本回归的回复记录 |
| CA-S03 | poly 的 width 参数报错 | 区分几何采样与渲染线宽，检查函数签名、同名覆盖和 **_ignored |
| CA-S04 | bat 中文乱码 | 对照文件编码、代码页、BOM 与 LF/CRLF，讨论具体脚本 |
| CA-S05 | 项目全景 | 可见 schemas、contracts、gateway 等目录与码道工作界面 |

## 作者过程记录

`开发过程记录.md` 还记录跨端坐标契约、模型 JSON 提取与重试、Prompt 注入、bat 编码、GitHub 连接及规范化内容指纹等六组问题。`README-材料清单.md` 提供截图与交付对应关系。

两份原文分别报告约 2.5 天和约 2.9 天的节省时间，测试统计也有不同口径。本报告将这些数字保留为作者自述，未合并为独立测量结果。作者文档中的毫米单位与 pageIndex 协议属于该组契约工作，不能据此推定当前 Windows 原生笔迹全部改用毫米。

材料清单引用提交 `8156747`，当前工作区未取得该对象，且原包未提供该完整代码树。本次依据截图和作者记录说明开发过程，未将其测试或提交认定为本机重新验收。原记录中的“待截”是作者原稿状态，实际随包提供的五图以本表为准。
"""
    (SUPPLEMENT / "整理说明.md").write_text(note, encoding="utf-8")
    (SUPPLEMENT / "来源清单.json").write_text(json.dumps({"archive": SOURCE.name, "sha256": sha(SOURCE), "entries": entries, "screenshots": shots}, ensure_ascii=False, indent=2), encoding="utf-8")
    index_path = ROOT / "05-来源与索引/材料索引.json"
    index = json.loads(index_path.read_text(encoding="utf-8"))
    index["memberSupplement"] = {"author": "艾尼萨尔", "screenshots": shots, "documents": ["06-艾尼萨尔补充/开发过程记录.md", "06-艾尼萨尔补充/README-材料清单.md"], "note": "06-艾尼萨尔补充/整理说明.md"}
    index_path.write_text(json.dumps(index, ensure_ascii=False, indent=2), encoding="utf-8")
    page = ROOT / "index.html"
    text = page.read_text(encoding="utf-8")
    start = "<!-- member-supplement-start -->"
    end = "<!-- member-supplement-end -->"
    if start in text:
        text = text[:text.index(start)] + text[text.index(end) + len(end):]
    cards = "".join(f'<article><h3>{s["id"]} {html.escape(s["title"])}</h3><a href="{s["file"]}"><img loading="lazy" src="{s["file"]}" alt="{html.escape(s["title"])}"></a></article>' for s in shots)
    section = start + '<h2>艾尼萨尔补充 · 双端契约与 AI 网关</h2><p class="note">新增 5 张交互截图及 2 份作者原文档。截图中的可见代码、回复与目录用于说明开发过程，测试及效率数字保留为作者自述。<a href="06-艾尼萨尔补充/整理说明.md">阅读整理说明 ↗</a></p><section class="grid">' + cards + '</section><p><a href="06-艾尼萨尔补充/开发过程记录.md">开发过程原文 ↗</a> · <a href="06-艾尼萨尔补充/README-材料清单.md">作者材料清单 ↗</a></p>' + end
    text = text.replace("</main>", section + "</main>")
    text = text.replace("共 8 张交互截图、6 份过程案例和 3 段运行录屏。", "现含 13 张交互截图、6 份过程案例、2 份成员原文档和 3 段运行录屏。")
    page.write_text(text, encoding="utf-8")
    readme = ROOT / "README.md"
    text = readme.read_text(encoding="utf-8")
    marker = "## 艾尼萨尔补充材料"
    if marker in text:
        text = text[:text.index(marker)]
    text += "\n" + marker + "\n\n新增 5 张函数解释、校验代码生成、报错和项目全景截图，以及 2 份作者原文，汇总后为 13 张截图。详见 [成员补充整理说明](06-艾尼萨尔补充/整理说明.md)。原文中的测试与节省时间为作者报告，本次未重复验收，未取得原文引用提交的对象。\n"
    readme.write_text(text, encoding="utf-8")
    print("Added five screenshots and two member documents, preserving originals")


if __name__ == "__main__":
    main()
