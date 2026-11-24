"""Import reviewed native captures from the adjoining product demo project."""

from pathlib import Path
import hashlib
import json
import shutil

import cv2
from PIL import Image


MANUAL = Path(__file__).resolve().parents[1]
SOURCE = MANUAL.parent / "product-demo" / "public" / "rebuild"
ASSETS = MANUAL / "public" / "assets"
CASES = ASSETS / "cases"
MEDIA = ASSETS / "media"

IMAGES = [
    "advanced-writing-final.png",
    "gaokao-question18.png",
    "gaokao-pdf-final.png",
    "shape-catalog-final.png",
    "geometry3d-final.png",
    "assistant-real-final.png",
    "classroom-final.png",
]
VIDEOS = [
    "advanced-writing",
    "gaokao-pdf",
    "shape-catalog",
    "geometry3d",
    "assistant-real",
    "classroom",
]
DOCUMENTS = ["高考18题-解析几何板书.icink", "高考数学全国一卷.pdf"]


def copy_asset(source, target):
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source, target)
    return {
        "source": source.relative_to(MANUAL.parent).as_posix(),
        "asset": target.relative_to(MANUAL).as_posix(),
        "bytes": target.stat().st_size,
        "sha256": hashlib.sha256(target.read_bytes()).hexdigest(),
    }


def main():
    records = []
    for name in IMAGES:
        records.append(copy_asset(SOURCE / name, CASES / name))
    for name in DOCUMENTS:
        records.append(copy_asset(SOURCE / name, CASES / name))
    for name in VIDEOS:
        record = copy_asset(SOURCE / f"{name}.mp4", MEDIA / f"{name}.mp4")
        capture = cv2.VideoCapture(str(SOURCE / f"{name}.mp4"))
        frame_count = capture.get(cv2.CAP_PROP_FRAME_COUNT)
        rate = capture.get(cv2.CAP_PROP_FPS)
        record["durationSeconds"] = round(frame_count / rate, 3)
        if name == "geometry3d":
            for percent in [15, 65]:
                capture.set(cv2.CAP_PROP_POS_FRAMES, int(frame_count * percent / 100))
                success, frame = capture.read()
                if not success:
                    raise RuntimeError(f"Cannot read {name} at {percent}%")
                Image.fromarray(cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)).save(
                    CASES / f"geometry3d-{percent}.jpg", quality=94
                )
        capture.release()
        records.append(record)
    # This crop retains the actual board content and removes its navigation chrome.
    board = Image.open(SOURCE / "advanced-writing-final.png")
    board.crop((24, 30, 1850, 955)).save(CASES / "ellipse-board-detail.png")
    assistant = Image.open(SOURCE / "assistant-real-final.png")
    assistant.crop((0, 0, 982, 620)).save(CASES / "assistant-response-detail.png")
    manifest = {
        "description": "Native captures and demonstration clips for the advanced classroom cases",
        "sourceReadme": "product-demo/README.md",
        "records": records,
        "derivedImages": {
            "ellipse-board-detail.png": {
                "source": "advanced-writing-final.png",
                "cropPixels": [24, 30, 1850, 955],
            },
            "geometry3d-15.jpg": {"source": "geometry3d.mp4", "progressPercent": 15},
            "geometry3d-65.jpg": {"source": "geometry3d.mp4", "progressPercent": 65},
            "assistant-response-detail.png": {
                "source": "assistant-real-final.png",
                "cropPixels": [0, 0, 982, 620],
            },
        },
    }
    (CASES / "source-manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(json.dumps({"imported": len(records), "clips": len(VIDEOS)}, ensure_ascii=False))


if __name__ == "__main__":
    main()
