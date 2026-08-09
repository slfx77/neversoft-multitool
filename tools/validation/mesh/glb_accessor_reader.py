#!/usr/bin/env python3
"""Minimal, correct GLB accessor reader (handles byteStride + normalization).

Importable helper for the other diagnostics in this folder.

    from glb_accessor_reader import load_glb, read_accessor
"""

from __future__ import annotations

import json
import struct

import numpy as np

_COMPONENT = {
    5120: ("i1", 1),
    5121: ("u1", 1),
    5122: ("<i2", 2),
    5123: ("<u2", 2),
    5125: ("<u4", 4),
    5126: ("<f4", 4),
}
_TYPE_COUNT = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}


def load_glb(path):
    with open(path, "rb") as handle:
        handle.read(12)
        json_len, _ = struct.unpack("<II", handle.read(8))
        js = json.loads(handle.read(json_len).decode("utf-8").rstrip("\x00"))
        rest = handle.read()
    # walk remaining chunks for BIN
    off = 0
    blob = b""
    while off + 8 <= len(rest):
        clen, ctype = struct.unpack("<II", rest[off : off + 8])
        payload = rest[off + 8 : off + 8 + clen]
        if ctype == 0x004E4942:  # 'BIN\0'
            blob = payload
            break
        off += 8 + clen
    return js, blob


def read_accessor(js, blob, index):
    acc = js["accessors"][index]
    n = _TYPE_COUNT[acc["type"]]
    dtype, size = _COMPONENT[acc["componentType"]]
    count = acc["count"]
    if "bufferView" not in acc:
        return np.zeros((count, n), dtype=np.float64)
    bv = js["bufferViews"][acc["bufferView"]]
    base = bv.get("byteOffset", 0) + acc.get("byteOffset", 0)
    stride = bv.get("byteStride") or (size * n)
    raw = np.frombuffer(blob, dtype=np.uint8, count=stride * (count - 1) + size * n, offset=base)
    view = np.lib.stride_tricks.as_strided(
        raw, shape=(count, size * n), strides=(stride, 1)
    ).copy()
    arr = view.view(np.dtype(dtype)).reshape(count, n).astype(np.float64)
    if acc.get("normalized"):
        denom = {5120: 127.0, 5121: 255.0, 5122: 32767.0, 5123: 65535.0}[acc["componentType"]]
        arr = np.maximum(arr / denom, -1.0)
    return arr
