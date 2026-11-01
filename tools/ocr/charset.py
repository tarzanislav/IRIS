"""The characters the IRIS OCR reads (Slovak, Czech, English and common signs). Index 0 is the CTC blank; the order
is part of the model file, so C# reads it from there – this list is only where it is defined."""

LOWER = "abcdefghijklmnopqrstuvwxyz" + "áäčďéěíĺľňóôöŕřšťúůüýž" + "ëß"
UPPER = "ABCDEFGHIJKLMNOPQRSTUVWXYZ" + "ÁÄČĎÉĚÍĹĽŇÓÔÖŔŘŠŤÚŮÜÝŽ" + "Ë"
DIGITS = "0123456789"
SIGNS = " .,;:!?-–—'\"„“”‚‘’()[]/\\%&*+=<>@#$€£§°…·•_|~^"
CHARS = LOWER + UPPER + DIGITS + SIGNS
assert len(set(CHARS)) == len(CHARS), "a character is listed twice"
INDEX = {c: i + 1 for i, c in enumerate(CHARS)}  # 0 = blank

# what a source text may contain that the charset writes another way
NORMALIZE = {" ": " ", " ": " ", " ": " ", "−": "-", "‐": "-", "‑": "-", "­": "",
             "«": "„", "»": "“", "″": "\"", "′": "'", "\t": " "}


def clean(text: str) -> str:
    """The text with only charset characters (others dropped) and single spaces."""
    text = "".join(NORMALIZE.get(c, c) for c in text)
    text = "".join(c for c in text if c in INDEX)
    return " ".join(text.split())


def encode(text: str) -> list:
    return [INDEX[c] for c in text]
