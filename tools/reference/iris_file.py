"""Writes IRIS model files (.iris) – the same layout as Iris.Model.ModelFile in C#:
"IRISMDL1", the length of the header (8 bytes, little endian), the header as JSON, then the data, every item on a
64-byte boundary (positions counted from the start of the data, which itself starts on a 64-byte boundary).
"""
import json
import struct

import numpy as np

MAGIC = b"IRISMDL1"
ALIGNMENT = 64


def _align(value: int) -> int:
    return (value + ALIGNMENT - 1) // ALIGNMENT * ALIGNMENT


def write(path, kind: str, config: dict, provenance: dict, tensors: dict, blobs: dict) -> None:
    """tensors: name → float32 array (any shape); blobs: name → bytes."""
    data = bytearray()
    items = []

    def place(name, dtype, shape, raw):
        offset = _align(len(data))
        data.extend(b"\0" * (offset - len(data)))
        data.extend(raw)
        items.append({"name": name, "dtype": dtype, "shape": list(shape), "offset": offset, "length": len(raw)})

    for name, array in tensors.items():
        array = np.ascontiguousarray(array, dtype="<f4")
        place(name, "f32", array.shape, array.tobytes())
    tensor_items = items
    items = []
    for name, raw in blobs.items():
        place(name, "u8", [len(raw)], bytes(raw))
    blob_items = items

    header = {"format": 1, "kind": kind, "config": config, "provenance": provenance,
              "tensors": tensor_items, "blobs": blob_items}
    header_bytes = json.dumps(header, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    with open(path, "wb") as f:
        f.write(MAGIC)
        f.write(struct.pack("<q", len(header_bytes)))
        f.write(header_bytes)
        f.write(b"\0" * (_align(16 + len(header_bytes)) - (16 + len(header_bytes))))
        f.write(data)
