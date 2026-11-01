"""Test pictures for the image readers and the page cleaner of IRIS (tests/Iris.Tests/testdata/vision).

Made once with Pillow and NumPy (only here, as tools – never in IRIS) from invented sentences and the DejaVu font
(free licence); the expected gray values come from Pillow, so the own PNG and JPEG readers are compared with another
implementation. Run: python3 tools/vision/make_testdata.py [font.ttf]
"""
import gzip
import io
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

OUT = Path(__file__).resolve().parents[2] / "tests" / "Iris.Tests" / "testdata" / "vision"
FONT = sys.argv[1] if len(sys.argv) > 1 else "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
LINES = [
    "Svetlo sa láme na rozhraní dvoch prostredí.",
    "Každá bunka má jadro, membránu a cytoplazmu.",
    "Ťažké kovy sa ukladajú v pôde a vo vode.",
    "Příčná vlna se šíří rychlostí světla.",
    "The cell divides when it reaches its full size.",
    "Ödön a Šárka čítajú ďalšiu kapitolu.",
]


def page(size=(980, 470), font_size=30):
    image = Image.new("L", size, 255)
    draw = ImageDraw.Draw(image)
    font = ImageFont.truetype(FONT, font_size)
    for i, line in enumerate(LINES):
        draw.text((40, 30 + i * 70), line, fill=20, font=font)
    return image


def expected(name, gray: np.ndarray, meta: dict, manifest: dict):
    (OUT / f"{name}.gray.gz").write_bytes(gzip.compress(gray.astype(np.uint8).tobytes(), 9, mtime=0))
    manifest[name] = dict(meta, width=int(gray.shape[1]), height=int(gray.shape[0]))


def luma(rgb: np.ndarray) -> np.ndarray:
    r, g, b = (rgb[..., i].astype(np.int64) for i in range(3))
    return ((r * 19595 + g * 38470 + b * 7471 + 32768) >> 16).astype(np.uint8)


def on_white(gray: np.ndarray, alpha: np.ndarray) -> np.ndarray:
    g, a = gray.astype(np.int64), alpha.astype(np.int64)
    return ((g * a + 255 * (255 - a) + 127) // 255).astype(np.uint8)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    manifest = {}
    base = page()
    gray = np.asarray(base)

    base.save(OUT / "page.png", optimize=True)
    expected("page.png", gray, {"lines": len(LINES)}, manifest)

    # colour kinds of PNG: RGB with a tint, RGBA, gray + alpha, palette, 16-bit gray
    rgb = np.stack([gray, np.clip(gray.astype(int) - 10, 0, 255), np.clip(gray.astype(int) + 5, 0, 255)], axis=-1).astype(np.uint8)
    Image.fromarray(rgb, "RGB").save(OUT / "rgb.png")
    expected("rgb.png", luma(rgb), {}, manifest)
    alpha = np.tile(np.linspace(80, 255, gray.shape[1]).astype(np.uint8), (gray.shape[0], 1))
    Image.fromarray(np.dstack([rgb, alpha]), "RGBA").save(OUT / "rgba.png")
    expected("rgba.png", on_white(luma(rgb), alpha), {}, manifest)
    Image.fromarray(np.dstack([gray, alpha]), "LA").save(OUT / "la.png")
    expected("la.png", on_white(gray, alpha), {}, manifest)
    quantized = base.quantize(16)
    quantized.save(OUT / "palette.png")
    pal = np.asarray(quantized.getpalette()[:48], dtype=np.uint8).reshape(-1, 3)
    expected("palette.png", luma(pal[np.asarray(quantized)]), {}, manifest)
    Image.fromarray(gray.astype(np.uint16) * 257).save(OUT / "gray16.png")
    expected("gray16.png", gray, {}, manifest)

    # a photo: turned by 3.5° (lines rising to the right), uneven light, colour, saved as a baseline JPEG 4:2:0
    turned = base.rotate(3.5, resample=Image.BICUBIC, expand=False, fillcolor=255)
    t = np.asarray(turned).astype(float)
    shade = np.linspace(0.75, 1.0, t.shape[1])[None, :] * np.linspace(0.85, 1.0, t.shape[0])[:, None]
    photo = np.clip(t * shade, 0, 255)
    photo_rgb = np.stack([photo, photo * 0.97, photo * 0.9], axis=-1).astype(np.uint8)
    buffer = io.BytesIO()
    Image.fromarray(photo_rgb, "RGB").save(buffer, "JPEG", quality=88, subsampling=2, progressive=False)
    (OUT / "photo.jpg").write_bytes(buffer.getvalue())
    decoded = Image.open(io.BytesIO(buffer.getvalue()))
    decoded.draft("L", decoded.size)  # libjpeg gives the brightness plane itself
    expected("photo.jpg", np.asarray(decoded.convert("L")), {"lines": len(LINES), "skew": 3.5}, manifest)

    # a gray JPEG with restart markers, and a small JPEG the camera stored sideways (EXIF orientation 6)
    buffer = io.BytesIO()
    base.save(buffer, "JPEG", quality=75, restart_marker_blocks=4)
    (OUT / "restart.jpg").write_bytes(buffer.getvalue())
    expected("restart.jpg", np.asarray(Image.open(io.BytesIO(buffer.getvalue()))), {}, manifest)
    small = base.resize((245, 118)).rotate(90, expand=True)  # stored turned left: shown after turning right
    exif = Image.Exif()
    exif[0x0112] = 6
    buffer = io.BytesIO()
    small.convert("RGB").save(buffer, "JPEG", quality=90, exif=exif.tobytes(), subsampling=0)
    (OUT / "sideways.jpg").write_bytes(buffer.getvalue())
    shown = Image.open(io.BytesIO(buffer.getvalue()))
    shown.draft("L", shown.size)
    expected("sideways.jpg", np.rot90(np.asarray(shown.convert("L")), k=-1), {"orientation": 6}, manifest)

    (OUT / "expected.json").write_text(json.dumps(manifest, indent=1, sort_keys=True) + "\n", encoding="utf-8")
    total = sum(p.stat().st_size for p in OUT.iterdir())
    print(f"{len(manifest)} pictures, {total / 1024:.0f} KB in {OUT}")


if __name__ == "__main__":
    main()
