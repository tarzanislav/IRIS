"""Pictures of lines of text as the IRIS OCR sees them: rendered with a free font, then spoiled like a photo or scan
(blur, low resolution, JPEG, noise, shadow, uneven ink, a slight slant) and cut out the way LineFinder cuts lines
(ink with a margin of a quarter of the line height), finally scaled to the height of the model."""
import io
import random
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

HEIGHT = 32


def find_fonts(folders=("/usr/share/fonts",)) -> list:
    """Free fonts that have every letter of the charset (checked by drawing the Slovak and Czech letters)."""
    from charset import CHARS

    found = []
    for folder in folders:
        for path in sorted(Path(folder).rglob("*")):
            if path.suffix.lower() not in (".ttf", ".otf"):
                continue
            try:
                font = ImageFont.truetype(str(path), 32)
            except OSError:
                continue
            # a font that lacks a glyph draws the same box for it: every letter must differ from the "missing" box
            missing = font.getmask("￿").getbbox()
            ok = all(font.getmask(c).getbbox() not in (None, missing) or c == " " for c in CHARS if c.strip())
            if ok:
                found.append(str(path))
    return found


def to_model(image: Image.Image) -> np.ndarray:
    """A gray line picture as model input: height HEIGHT, width kept in proportion (at least 8), ink = 1, paper = 0."""
    w, h = image.size
    width = max(8, round(w * HEIGHT / h))
    scaled = image.resize((width, HEIGHT), Image.BILINEAR if h < HEIGHT else Image.BOX)
    return 1.0 - np.asarray(scaled, dtype=np.float32) / 255.0


def render(text: str, fonts: list, r: random.Random) -> Image.Image:
    """A spoiled picture of a line of text, cut out like LineFinder does (gray, any size)."""
    font = ImageFont.truetype(r.choice(fonts), r.randint(16, 60))
    paper = r.randint(170, 255)
    ink = r.randint(0, min(110, paper - 60))
    left, top, right, bottom = font.getbbox(text)
    pad = font.size
    canvas = Image.new("L", (right - left + 2 * pad, bottom - top + 2 * pad), paper)
    ImageDraw.Draw(canvas).text((pad - left, pad - top), text, font=font, fill=ink)

    if r.random() < 0.15:  # bolder or thinner ink
        canvas = canvas.filter(ImageFilter.MinFilter(3) if r.random() < 0.5 else ImageFilter.MaxFilter(3))
    if r.random() < 0.5:  # a slight slant left after straightening
        canvas = canvas.rotate(r.uniform(-1.2, 1.2), resample=Image.BICUBIC, expand=True, fillcolor=paper)

    a = np.asarray(canvas, dtype=np.float32)
    h, w = a.shape
    if r.random() < 0.6:  # uneven light: a shadow across the line
        gradient = np.linspace(r.uniform(0.7, 1.0), r.uniform(0.85, 1.05), w)[None, :]
        a = a * gradient * np.linspace(r.uniform(0.9, 1.0), 1.0, h)[:, None]
    if r.random() < 0.5:  # paper texture
        a = a + np.random.default_rng(r.randrange(1 << 30)).normal(0, r.uniform(1, 10), a.shape)
    image = Image.fromarray(np.clip(a, 0, 255).astype(np.uint8))

    if r.random() < 0.5:
        image = image.filter(ImageFilter.GaussianBlur(r.uniform(0.2, 1.3) * font.size / 32))
    if r.random() < 0.4:  # a photo from afar: few pixels per letter
        factor = r.uniform(0.25, 0.7)
        small = image.resize((max(1, int(image.width * factor)), max(1, int(image.height * factor))), Image.BOX)
        image = small.resize(image.size, Image.BILINEAR)
    if r.random() < 0.5:
        buffer = io.BytesIO()
        image.save(buffer, "JPEG", quality=r.randint(25, 90))
        image = Image.open(io.BytesIO(buffer.getvalue())).convert("L")

    # cut out as LineFinder does: the ink's box with a margin of a quarter of its height (± a little)
    a = np.asarray(image)
    dark = a < (int(a.mean()) - 30)
    rows, cols = np.where(dark.any(axis=1))[0], np.where(dark.any(axis=0))[0]
    if len(rows) == 0 or len(cols) == 0:
        return image
    top, bottom, left, right = rows[0], rows[-1] + 1, cols[0], cols[-1] + 1
    margin = max(2, (bottom - top) // 4)
    jitter = lambda: int(margin * r.uniform(-0.3, 0.3))  # noqa: E731
    return image.crop((max(0, left - margin + jitter()), max(0, top - margin + jitter()),
                       min(image.width, right + margin + jitter()), min(image.height, bottom + margin + jitter())))
