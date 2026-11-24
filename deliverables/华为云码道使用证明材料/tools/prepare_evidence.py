"""Read supplied CodeArts logs and create a bounded, redacted evidence index."""

from pathlib import Path
import hashlib
import io
import json
import re
import zipfile


ROOT = Path(__file__).resolve().parents[1]
SOURCE = Path(r"C:\Users\Administrator\Downloads\码道鸿蒙")
OUT = ROOT / "05-来源与索引"


def redact(text):
    text = re.sub(r"(?i)Bearer\s+[A-Za-z0-9._~+/-]+", "Bearer [凭据已隐去]", text)
    text = re.sub(r"\bsk-[A-Za-z0-9_-]{12,}", "[密钥已隐去]", text)
    text = re.sub(r"\b(?=[A-Z0-9]{15,18}\b)(?=[A-Z0-9]*\d)[A-Z0-9]+\b", "[设备标识已隐去]", text)
    text = re.sub(r"(?i)([A-Z]:[/\\]Users[/\\])[^/\\\s\"<>]+", r"\1[用户]", text)
    text = re.sub(r"\b(?:\d{1,3}\.){3}\d{1,3}\b(?::\d{2,5})?", "[网络地址已隐去]", text)
    text = re.sub(r"https?://[^\s\"<>]+", "[链接已隐去]", text)
    return text


def extract_event(line):
    if 'service=bus-publish "{' not in line:
        return None
    start = line.find("{")
    end = line.rfind("}") + 1
    try:
        event = json.loads(line[start:end])
    except (ValueError, TypeError):
        return extract_partial_event(line[start:end])
    if event.get("type") != "message.part.updated":
        return None
    part = event.get("properties", {}).get("part", {})
    # Reasoning and memory metadata are excluded from the evidence export.
    if part.get("type") == "text":
        return {
            "id": part.get("id"),
            "message": part.get("messageID"),
            "session": part.get("sessionID"),
            "kind": "prompt" if part.get("metadata", {}).get("isUserInput") else "assistant",
            "text": redact(part.get("text", "")),
        }
    if part.get("type") != "tool":
        return None
    state = part.get("state", {})
    if state.get("status") not in ("completed", "error"):
        return None
    name = part.get("tool", "")
    if name not in ("edit", "write", "bash", "read", "glob", "grep"):
        return None
    inputs = state.get("input", {})
    allowed = ("filePath", "command", "description", "oldString", "newString", "content", "pattern", "path", "offset", "limit")
    safe_input = {key: redact(str(value)) for key, value in inputs.items() if key in allowed}
    return {
        "id": part.get("id"),
        "message": part.get("messageID"),
        "session": part.get("sessionID"),
        "kind": "tool",
        "tool": name,
        "status": state.get("status"),
        "input": safe_input,
        "output": redact(str(state.get("output", state.get("error", "")))),
    }


def decode_logged_string(value):
    escapes = {'n': '\n', 'r': '\r', 't': '\t', 'b': '\b', 'f': '\f', '"': '"', '\\': '\\', '/': '/'}
    return re.sub(r'\\(u[0-9a-fA-F]{4}|[nrtbf"\\/])',
                  lambda match: chr(int(match.group(1)[1:], 16)) if match.group(1).startswith('u') else escapes[match.group(1)], value)


def extract_partial_event(payload):
    """Some logger records contain unescaped strings in metadata. Read bounded fields."""
    if '"type":"message.part.updated"' not in payload:
        return None
    part_at = payload.find('"part":')
    payload = payload[part_at:]
    def simple(key):
        match = re.search(r'"' + key + r'":"([^"\r\n]*)"', payload)
        return match.group(1) if match else ""
    event = {"id": simple("id"), "message": simple("messageID"), "session": simple("sessionID"), "partialRecord": True}
    kind = simple("type")
    if kind == "text":
        match = re.search(r'"text":"(.*?)"(?:,"(?:messageID|sessionID|id|metadata)"|},"time")', payload, re.DOTALL)
        if not match:
            return None
        event.update(kind="prompt" if '"isUserInput":true' in payload else "assistant", text=redact(decode_logged_string(match.group(1))))
        return event
    if kind != "tool" or simple("tool") not in ("edit", "write", "bash", "read", "glob", "grep"):
        return None
    status = simple("status")
    if status not in ("completed", "error"):
        return None
    safe = {}
    keys = "filePath|command|description|oldString|newString|content|pattern|path|offset|limit|replaceAll|output|raw|title|metadata|time|error"
    for key in ["filePath", "command", "description", "oldString", "newString", "content"]:
        match = re.search(r'"' + key + r'":"(.*?)"(?:,"(?:' + keys + r')"|},"output"|},"raw"|}})', payload, re.DOTALL)
        if match:
            safe[key] = redact(decode_logged_string(match.group(1)))
    output = re.search(r'"output":"(.*?)"(?:,"(?:title|metadata|time)"|}},"time")', payload, re.DOTALL)
    event.update(kind="tool", tool=simple("tool"), status=status, input=safe,
                 output=redact(decode_logged_string(output.group(1))) if output else "")
    return event


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    archive = SOURCE / "码道记录-全量.zip"
    entries = []
    events = {}
    parsed = 0
    with zipfile.ZipFile(archive) as package:
        for item in package.infolist():
            if item.is_dir():
                continue
            basename = item.filename.rsplit("/", 1)[-1]
            entries.append({"name": basename, "bytes": item.file_size, "crc32": f"{item.CRC:08x}"})
            if not basename.startswith("kernel-codeartsdoer-incognito-"):
                continue
            with package.open(item) as stream:
                for number, raw in enumerate(stream, 1):
                    if b"service=bus-publish" not in raw:
                        continue
                    line = raw.decode("utf-8", errors="replace")
                    event = extract_event(line)
                    if not event or not event.get("id"):
                        continue
                    event["source"] = basename
                    event["line"] = number
                    stamp = re.search(r"\d{4}-\d{2}-\d{2}T[\d:.]+", line[:150])
                    event["logTime"] = stamp.group() if stamp else ""
                    # Updates to a part replace earlier partial versions.
                    events[event["id"]] = event
                    parsed += 1
    items = sorted(events.values(), key=lambda event: (event["logTime"], event["source"], event["line"]))
    inventory = {
        "sourceArchive": archive.name,
        "sha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
        "compressedBytes": archive.stat().st_size,
        "uncompressedBytes": sum(item["bytes"] for item in entries),
        "files": entries,
        "note": "日志时间按原日志保留，未推断时区。解码使用UTF-8，压缩包目录显示名省略。",
    }
    (OUT / "源文件清单.json").write_text(json.dumps(inventory, ensure_ascii=False, indent=2), encoding="utf-8")
    # Intermediate index stays outside the submission folder.
    private = ROOT.parent / "product-manual" / "source-assets" / "codearts"
    private.mkdir(parents=True, exist_ok=True)
    (private / "events.json").write_text(json.dumps(items, ensure_ascii=False), encoding="utf-8")
    prompts = [event for event in items if event["kind"] == "prompt"]
    (private / "prompts.json").write_text(json.dumps(prompts, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"sourceFiles": len(entries), "updates": parsed, "uniqueEvents": len(items), "prompts": len(prompts)}, ensure_ascii=False))
    for index, event in enumerate(prompts):
        print(index, event["logTime"], event["source"], event["text"][:180].replace("\n", " "))


if __name__ == "__main__":
    main()
