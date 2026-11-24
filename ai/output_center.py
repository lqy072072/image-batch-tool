#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""专业输出中心：联系表、批量水印/边框、ICC 软打样。

命令：
  output_center.py contact input.json output.json
  output_center.py watermark input.json output.json
  output_center.py proof input.json output.json

输出 JSON 只返回文件路径与统计，不返回 base64，避免 WebView 消息过大。
"""
import json
import os
import subprocess
import sys
import traceback

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont, ImageOps, ImageCms

from imgio import imread_u


def _font(size, bold=False):
    candidates = [
        r"C:\Windows\Fonts\msyhbd.ttc" if bold else r"C:\Windows\Fonts\msyh.ttc",
        r"C:\Windows\Fonts\simhei.ttf",
        r"C:\Windows\Fonts\arialbd.ttf" if bold else r"C:\Windows\Fonts\arial.ttf",
    ]
    for p in candidates:
        try:
            if os.path.exists(p):
                return ImageFont.truetype(p, size)
        except Exception:
            pass
    return ImageFont.load_default()


def _open_rgb(path):
    im = Image.open(path)
    im = ImageOps.exif_transpose(im)
    return im.convert("RGB")


def _hex_rgb(s):
    s = (s or "#ffffff").strip().lstrip("#")
    if len(s) == 3:
        s = "".join(c * 2 for c in s)
    try:
        return tuple(int(s[i:i + 2], 16) for i in (0, 2, 4))
    except Exception:
        return (255, 255, 255)


def contact(cfg):
    paths = cfg.get("paths") or []
    if not paths:
        raise RuntimeError("没有输入图片")
    out = cfg.get("output") or os.path.join(os.path.dirname(paths[0]), "_contact_sheet.jpg")
    cols = max(1, min(10, int(cfg.get("cols", 4))))
    cell = max(120, min(1200, int(cfg.get("cell", 360))))
    gap = max(4, min(80, int(cfg.get("gap", 14))))
    margin = max(8, min(100, int(cfg.get("margin", 28))))
    label_h = 34 if cfg.get("labels", True) else 0
    title = str(cfg.get("title") or "联系表")
    bg = _hex_rgb(cfg.get("bg", "#16181d"))
    fg = _hex_rgb(cfg.get("fg", "#e8ebf0"))
    rows = (len(paths) + cols - 1) // cols
    W = margin * 2 + cols * cell + (cols - 1) * gap
    H = margin * 2 + 58 + rows * (cell + label_h) + (rows - 1) * gap
    sheet = Image.new("RGB", (W, H), bg)
    d = ImageDraw.Draw(sheet)
    d.text((margin, margin), title, font=_font(22, True), fill=fg)
    d.text((margin, margin + 30), "%d 张 · %d 列 · %s" % (len(paths), cols, cfg.get("subtitle", "")),
           font=_font(11), fill=fg)
    top = margin + 58
    for i, p in enumerate(paths):
        r, c = divmod(i, cols)
        x = margin + c * (cell + gap)
        y = top + r * (cell + label_h + gap)
        try:
            im = _open_rgb(p)
            im.thumbnail((cell, cell), Image.LANCZOS)
            tile = Image.new("RGB", (cell, cell), (28, 30, 35))
            ox = (cell - im.width) // 2
            oy = (cell - im.height) // 2
            tile.paste(im, (ox, oy))
        except Exception:
            tile = Image.new("RGB", (cell, cell), (45, 30, 30))
            d.text((8, 8), "读取失败", font=_font(12), fill=(255, 160, 160))
        sheet.paste(tile, (x, y))
        if label_h:
            name = os.path.basename(p)
            d.text((x + 2, y + cell + 7), name[:max(10, cell // 6)], font=_font(10), fill=fg)
    os.makedirs(os.path.dirname(out) or ".", exist_ok=True)
    sheet.save(out, quality=94, subsampling=1)
    return {"ok": True, "output": out, "count": len(paths), "w": W, "h": H}


def watermark(cfg):
    paths = cfg.get("paths") or []
    if not paths:
        raise RuntimeError("没有输入图片")
    text = str(cfg.get("text") or "图片批处理工具")
    pos = str(cfg.get("position") or "bottom-right")
    opacity = max(0.0, min(1.0, float(cfg.get("opacity", 0.55))))
    size_raw = int(cfg.get("size", 0) or 0)
    size = max(10, min(200, size_raw)) if size_raw else 0
    border = max(0, min(80, int(cfg.get("border", 0))))
    outdir = cfg.get("outdir") or os.path.join(os.path.dirname(paths[0]), "_watermark")
    os.makedirs(outdir, exist_ok=True)
    logo_path = cfg.get("logo") or ""
    results = []
    for p in paths:
        try:
            im = _open_rgb(p)
            W, H = im.size
            if border:
                im = ImageOps.expand(im, border=border, fill=_hex_rgb(cfg.get("border_color", "#ffffff")))
                W, H = im.size
            layer = Image.new("RGBA", im.size, (0, 0, 0, 0))
            d = ImageDraw.Draw(layer)
            fs = size or max(12, int(min(W, H) * 0.035))
            font = _font(fs, bool(cfg.get("bold", False)))
            bbox = d.textbbox((0, 0), text, font=font)
            tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
            pad = max(8, fs // 2)
            if "left" in pos:
                x = pad
            elif "center" in pos:
                x = (W - tw) // 2
            else:
                x = W - tw - pad
            if "top" in pos:
                y = pad
            elif "center" in pos:
                y = (H - th) // 2
            else:
                y = H - th - pad
            if logo_path and os.path.exists(logo_path):
                logo = _open_rgb(logo_path).convert("RGBA")
                lw = max(24, int(min(W, H) * 0.12))
                logo.thumbnail((lw, lw), Image.LANCZOS)
                lx = pad if "left" in pos else (W - logo.width - pad if "right" in pos else (W - logo.width) // 2)
                ly = pad if "top" in pos else (H - logo.height - pad - th - 6 if "bottom" in pos else (H - logo.height) // 2)
                layer.alpha_composite(logo, (lx, ly))
                if "bottom" in pos:
                    y = H - th - pad
            fill = _hex_rgb(cfg.get("color", "#ffffff")) + (int(255 * opacity),)
            # 轻微描边，保证白字在天空/白墙上可见
            for dx, dy in ((-1, 0), (1, 0), (0, -1), (0, 1)):
                d.text((x + dx, y + dy), text, font=font, fill=(0, 0, 0, int(150 * opacity)))
            d.text((x, y), text, font=font, fill=fill)
            im = Image.alpha_composite(im.convert("RGBA"), layer).convert("RGB")
            stem, ext = os.path.splitext(os.path.basename(p))
            dst = os.path.join(outdir, stem + "_wm.jpg")
            im.save(dst, quality=int(cfg.get("quality", 95)), subsampling=1)
            results.append({"ok": True, "output": dst, "input": p, "w": W, "h": H})
        except Exception as e:
            results.append({"ok": False, "input": p, "error": str(e)})
    first = next((x.get("output") for x in results if x.get("ok")), None)
    return {"ok": any(x["ok"] for x in results), "output": first, "count": len(results), "outdir": outdir, "results": results}


def proof(cfg):
    src = cfg.get("path") or ""
    out = cfg.get("output") or os.path.join(os.path.dirname(src), "_proof.jpg")
    icc = cfg.get("icc") or r"C:\Windows\System32\spool\drivers\color\sRGB Color Space Profile.icm"
    intent_name = str(cfg.get("intent") or "relative").lower()
    intent = ImageCms.Intent.RELATIVE_COLORIMETRIC if "rel" in intent_name else ImageCms.Intent.PERCEPTUAL
    if not os.path.exists(src):
        raise RuntimeError("找不到输入图片")
    if not os.path.exists(icc):
        raise RuntimeError("找不到 ICC 配置文件")
    im = _open_rgb(src)
    try:
        src_profile = ImageCms.createProfile("sRGB")
        dst_profile = ImageCms.getOpenProfile(icc)
        # 软打样：模拟目标 ICC 后再转回 sRGB 显示；不修改原始文件
        to_target = ImageCms.profileToProfile(im, src_profile, dst_profile, renderingIntent=intent, outputMode="RGB")
        back = ImageCms.profileToProfile(to_target, dst_profile, src_profile, renderingIntent=intent, outputMode="RGB")
    except Exception as e:
        raise RuntimeError("ICC 转换失败：" + str(e))
    os.makedirs(os.path.dirname(out) or ".", exist_ok=True)
    back.save(out, quality=95, subsampling=1, icc_profile=ImageCms.ImageCmsProfile(ImageCms.createProfile("sRGB")).tobytes())
    return {"ok": True, "output": out, "icc": icc, "intent": intent_name}


def main():
    if len(sys.argv) < 4:
        print(json.dumps({"ok": False, "error": "usage: output_center.py contact|watermark|proof input output"}))
        return 2
    cmd, inp, outp = sys.argv[1].lower(), sys.argv[2], sys.argv[3]
    try:
        cfg = json.load(open(inp, "r", encoding="utf-8"))
        fn = {"contact": contact, "watermark": watermark, "proof": proof}.get(cmd)
        if not fn:
            raise RuntimeError("unknown command")
        r = fn(cfg)
        json.dump(r, open(outp, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
        print(json.dumps({"ok": r.get("ok"), "output": r.get("output"), "count": r.get("count")},
                         ensure_ascii=False))
        return 0 if r.get("ok") else 1
    except Exception as e:
        r = {"ok": False, "error": str(e), "tb": traceback.format_exc()[-800:]}
        try:
            json.dump(r, open(outp, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
        except Exception:
            pass
        print(json.dumps(r, ensure_ascii=False))
        return 1


if __name__ == "__main__":
    sys.exit(main())
