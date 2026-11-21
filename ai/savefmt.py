#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 GDI+ 生成的临时 PNG 编码成 WebP（GDI+ 没有 WebP 编码器）。"""
import json
import os
import sys

from PIL import Image


def main():
    if len(sys.argv) < 5:
        print(json.dumps({"ok": False, "error": "args"}))
        return 2
    src, dst, fmt, quality = sys.argv[1], sys.argv[2], sys.argv[3].lower(), sys.argv[4]
    try:
        q = max(1, min(100, int(float(quality))))
    except Exception:
        q = 95
    try:
        im = Image.open(src)
        if im.mode not in ("RGB", "RGBA"):
            im = im.convert("RGB")
        if fmt == "webp":
            im.save(dst, format="WEBP", quality=q, method=6)
        elif fmt == "png":
            im.save(dst, format="PNG")
        else:
            im.save(dst, format="JPEG", quality=q, subsampling=0)
        print(json.dumps({"ok": True, "w": im.width, "h": im.height}))
        return 0
    except Exception as e:
        print(json.dumps({"ok": False, "error": str(e)}))
        return 1


if __name__ == "__main__":
    sys.exit(main())
