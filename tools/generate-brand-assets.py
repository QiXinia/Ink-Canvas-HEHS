"""Generate application and print assets from the supplied vector logo.

Requires Pillow and PyMuPDF. Runtime application code only consumes the assets.
"""

from io import BytesIO
from pathlib import Path
import argparse
import shutil
import xml.etree.ElementTree as ET

import pymupdf
from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
BRAND = ROOT / "Images" / "Brand"
APP = ROOT / "src" / "InkCanvas.App" / "Assets"
PALETTES = {"Light": ("#182F49", "#0067C0"), "Dark": ("#F2F6FC", "#60CDFF")}
VIEWBOX = "100 20 440 440"


def svg(paths, outline, ink, tile=False):
    background = '<rect x="102" y="22" width="436" height="436" rx="88" fill="#F5F8FF" stroke="#CED9E6" stroke-width="3"/>' if tile else ""
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="{VIEWBOX}">'
            f'{background}<path fill="{outline}" d="{paths[0]}"/>'
            f'<path fill="{ink}" d="{paths[1]}"/></svg>')


def raster(vector, size=1024):
    source = pymupdf.open(stream=vector.encode("utf-8"), filetype="svg")
    pdf = pymupdf.open("pdf", source.convert_to_pdf())
    pixmap = pdf[0].get_pixmap(matrix=pymupdf.Matrix(size / 440, size / 440), alpha=True)
    return Image.frombytes("RGBA", (pixmap.width, pixmap.height), pixmap.samples)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, default=BRAND / "Logo.Source.svg")
    args = parser.parse_args()
    BRAND.mkdir(parents=True, exist_ok=True)
    APP.mkdir(parents=True, exist_ok=True)
    if args.source.resolve() != (BRAND / "Logo.Source.svg").resolve():
        shutil.copyfile(args.source, BRAND / "Logo.Source.svg")
    source = ET.parse(BRAND / "Logo.Source.svg").getroot()
    paths = [item.attrib["d"] for item in source.findall("{http://www.w3.org/2000/svg}path")]
    if len(paths) != 3:
        raise ValueError("Expected the supplied background path, board outline and ink mark")
    # The first compound path is a white canvas. The last two are the authored mark.
    paths = paths[1:]
    for theme, (outline, ink) in PALETTES.items():
        vector = svg(paths, outline, ink)
        (BRAND / f"InkCanvas.{theme}.svg").write_text(vector, encoding="utf-8")
        image = raster(vector)
        image.save(BRAND / f"InkCanvas.{theme}.png")
        image.resize((128, 128), Image.Resampling.LANCZOS).save(APP / f"InkCanvas.{theme}.png")
        image.save(APP / f"InkCanvas.{theme}.ico", sizes=[(16,16),(20,20),(24,24),(32,32),(40,40),(48,48),(64,64),(128,128),(256,256)])
    shell = raster(svg(paths, *PALETTES["Light"], tile=True))
    shell.save(APP / "InkCanvas.ico", sizes=[(16,16),(20,20),(24,24),(32,32),(40,40),(48,48),(64,64),(128,128),(256,256)])
    shell.save(BRAND / "InkCanvas.App.png")
    outline, ink = PALETTES["Light"]
    responsive = svg(paths, outline, ink).replace(
        f'<path fill="{outline}"', '<style>.outline{fill:#182F49}.ink{fill:#0067C0}@media(prefers-color-scheme:dark){.outline{fill:#F2F6FC}.ink{fill:#60CDFF}}</style><path class="outline"'
    ).replace(f'<path fill="{ink}"', '<path class="ink"')
    (BRAND / "InkCanvas.svg").write_text(responsive, encoding="utf-8")
    print("Generated light/dark vectors, PNGs and multi-resolution Windows icons")


if __name__ == "__main__":
    main()
