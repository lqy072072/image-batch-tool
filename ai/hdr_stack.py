#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""HDR 曝光融合 / 焦点堆栈 / 降噪堆栈。

设计说明：
- HDR 使用 OpenCV AlignMTB + Mertens 曝光融合，避免把 8-bit JPEG 强行反推
  成线性 RAW 后产生巨大噪声；这是目前桌面工具里最稳的“一键出片”路线。
- 焦点堆栈使用局部拉普拉斯清晰度权重，适合三脚架/微距/景深合成。
- 降噪堆栈使用对齐后的中位数，适合夜景、星轨底图、手持连拍。
- analyze 只依赖 EXIF/时间/亮度/清晰度做自动分组，不联网；AI HDR 模型
  （AHDRNet/DeepHDR 等）通常需要 PyTorch 权重，不适合塞进当前
  onnxruntime 便携包，故不在这里伪造“AI HDR”。

用法：
  python hdr_stack.py analyze <input.json> <output.json>
  python hdr_stack.py merge   <input.json> <output_image>

input.json:
  {"paths":["a.jpg","b.jpg"], "mode":"auto|hdr|focus|noise",
   "align":true, "deghost":true, "deghost_strength":0.35,
   "auto_tone":true, "max_dim":6000}
"""
import json
import math
import os
import re
import statistics
import sys
from collections import Counter
from datetime import datetime

import cv2
import numpy as np
from PIL import Image, ExifTags

from imgio import imread_u, imwrite_u

MAX_DIM = 6000
EXIF_EXPOSURE = 33434
EXIF_FNUMBER = 33437
EXIF_ISO = 34855
EXIF_DATETIME = 36867


def _json_file(path):
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def _write_json(path, obj):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(obj, f, ensure_ascii=False, indent=2)


def _mean_luma(img):
    small = cv2.resize(img, (160, 160), interpolation=cv2.INTER_AREA)
    lab = cv2.cvtColor(small, cv2.COLOR_BGR2LAB)
    return float(np.mean(lab[:, :, 0]) / 255.0)


def _sharpness(img):
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    if max(gray.shape) > 900:
        s = 900.0 / max(gray.shape)
        gray = cv2.resize(gray, None, fx=s, fy=s, interpolation=cv2.INTER_AREA)
    return float(cv2.Laplacian(gray, cv2.CV_64F).var())


def _exif_info(path):
    info = {"exposure": None, "fnumber": None, "iso": None, "datetime": None}
    try:
        im = Image.open(path)
        exif = im.getexif()
        for tag, key in ((EXIF_EXPOSURE, "exposure"), (EXIF_FNUMBER, "fnumber"),
                         (EXIF_ISO, "iso"), (EXIF_DATETIME, "datetime")):
            v = exif.get(tag)
            if v is None:
                continue
            if key == "exposure":
                try:
                    info[key] = float(v)
                except Exception:
                    pass
            elif key == "fnumber":
                try:
                    info[key] = float(v)
                except Exception:
                    pass
            elif key == "iso":
                try:
                    info[key] = int(v)
                except Exception:
                    pass
            else:
                info[key] = str(v)
    except Exception:
        pass
    return info


def _file_time(path):
    try:
        return os.path.getmtime(path)
    except Exception:
        return 0.0


def _family_name(name):
    """相机连拍/包围曝光通常共享文件名前缀：IMG_0001、DSC_1234、bracket_0。
    去掉扩展名和结尾的数字/单字母，作为“同一组”的候选；不要只依赖时间，
    否则焦点堆栈和曝光包围会互相混在一起。
    """
    stem = os.path.splitext(name)[0]
    marker = re.sub(r"(?i)(?:[_\-\s]?(?:hdr|focus|stack|ev|bracket|shot|copy|副本))+$", "", stem)
    marker = re.sub(r"(?i)(?:[_\-\s]?\(?\d+\)?)$", "", marker)
    marker = re.sub(r"(?i)([_\-\s][a-z])$", "", marker)
    return marker.lower()


def _ev_value(exposure):
    if not exposure or exposure <= 0:
        return None
    return math.log2(1.0 / exposure)


def _same_size(a, b):
    if not a or not b:
        return False
    return abs(a[0] - b[0]) <= max(2, a[0] * 0.01) and abs(a[1] - b[1]) <= max(2, a[1] * 0.01)


def inspect_images(paths, max_dim=1200):
    items = []
    for p in paths:
        try:
            img = imread_u(p, cv2.IMREAD_COLOR)
            if img is None:
                continue
            h, w = img.shape[:2]
            scale = min(1.0, float(max_dim) / max(h, w))
            if scale < 1.0:
                img = cv2.resize(img, (max(1, int(w * scale)), max(1, int(h * scale))),
                                 interpolation=cv2.INTER_AREA)
            ex = _exif_info(p)
            items.append({
                "path": p,
                "name": os.path.basename(p),
                "dir": os.path.dirname(p),
                "family": _family_name(os.path.basename(p)),
                "mtime": _file_time(p),
                "w": w, "h": h,
                "luma": round(_mean_luma(img), 4),
                "sharpness": round(_sharpness(img), 2),
                "exposure": ex["exposure"],
                "ev": round(_ev_value(ex["exposure"]), 2) if _ev_value(ex["exposure"]) is not None else None,
                "fnumber": ex["fnumber"],
                "iso": ex["iso"],
                "datetime": ex["datetime"],
            })
        except Exception:
            continue
    return items


def analyze(paths, gap_seconds=90):
    """按目录、分辨率、时间间隔分组，再按曝光差/清晰度差给出建议模式。"""
    items = inspect_images(paths)
    items.sort(key=lambda x: (x["dir"], x["mtime"], x["name"]))
    # 相机文件名前缀是比分秒时间更可靠的“同一组”信号：
    # IMG_0001/0002/0003、DSC_1234、bracket_0、focus_l/focus_r。
    # 如果所有文件名都是散名，则退回只看目录/尺寸/时间。
    fam_counts = Counter(x["family"] for x in items)
    use_family = any(v >= 2 for v in fam_counts.values())
    groups = []
    cur = []
    for it in items:
        if not cur:
            cur = [it]
            continue
        prev = cur[-1]
        gap = abs(it["mtime"] - prev["mtime"])
        size_ok = _same_size((it["w"], it["h"]), (prev["w"], prev["h"]))
        family_ok = (not use_family) or it["family"] == prev["family"]
        if it["dir"] == prev["dir"] and size_ok and family_ok and gap <= (gap_seconds if len(cur) < 3 else gap_seconds * 2):
            cur.append(it)
        else:
            groups.append(cur)
            cur = [it]
    if cur:
        groups.append(cur)
    # 如果按文件名前缀切得太碎，允许把同目录、同尺寸、时间连续的组重新合并。
    if use_family and all(len(g) < 2 for g in groups):
        groups = []
        cur = []
        for it in items:
            if not cur:
                cur = [it]
                continue
            prev = cur[-1]
            if it["dir"] == prev["dir"] and _same_size((it["w"], it["h"]), (prev["w"], prev["h"])) and abs(it["mtime"] - prev["mtime"]) <= gap_seconds:
                cur.append(it)
            else:
                groups.append(cur)
                cur = [it]
        if cur:
            groups.append(cur)

    out = []
    for idx, g in enumerate(groups):
        if len(g) < 2:
            continue
        lumas = [x["luma"] for x in g]
        sharp = [x["sharpness"] for x in g]
        evs = [x["ev"] for x in g if x["ev"] is not None]
        luma_span = max(lumas) - min(lumas)
        sharp_span = (max(sharp) - min(sharp)) / max(1e-6, max(sharp))
        ev_span = max(evs) - min(evs) if len(evs) >= 2 else 0.0
        if len(g) >= 2 and (luma_span >= 0.16 or ev_span >= 1.5):
            kind = "hdr"
            confidence = min(0.99, 0.55 + luma_span * 1.4 + min(1.0, ev_span / 4.0) * 0.2)
            reason = ("曝光差异 %.0f%%，EV 跨度 %.1f" % (luma_span * 100, ev_span)) if evs else ("曝光差异 %.0f%%（无 EXIF EV 信息）" % (luma_span * 100))
        elif len(g) >= 2 and sharp_span >= 0.12:
            kind = "focus"
            confidence = min(0.99, 0.55 + sharp_span)
            reason = "清晰度差异 %.0f%%，同场景连续拍摄" % (sharp_span * 100)
        elif len(g) >= 3:
            kind = "noise"
            confidence = min(0.95, 0.45 + len(g) / 20.0)
            reason = "同曝光连拍 %d 张，适合降噪堆栈" % len(g)
        else:
            kind = "burst"
            confidence = 0.45
            reason = "同场景连拍，建议手动选择堆栈模式"
        out.append({
            "id": "g%d" % (idx + 1),
            "kind": kind,
            "suggested_mode": "hdr" if kind == "hdr" else ("focus" if kind == "focus" else "noise"),
            "confidence": round(confidence, 3),
            "reason": reason,
            "count": len(g),
            "paths": [x["path"] for x in g],
            "items": g,
            "meta": {
                "luma_min": round(min(lumas), 3), "luma_max": round(max(lumas), 3),
                "ev_min": round(min(evs), 2) if evs else None,
                "ev_max": round(max(evs), 2) if evs else None,
                "sharp_min": round(min(sharp), 1), "sharp_max": round(max(sharp), 1),
            },
        })
    out.sort(key=lambda x: (-{"hdr": 3, "focus": 2, "noise": 1, "burst": 0}.get(x["kind"], 0), -x["confidence"]))
    return {
        "ok": True,
        "count": len(items),
        "groups": out,
        "note": "自动分组使用 EXIF/时间/亮度/清晰度；可在界面上手动改模式。",
    }


def _load_stack(paths, max_dim=MAX_DIM):
    imgs = []
    for p in paths:
        im = imread_u(p, cv2.IMREAD_COLOR)
        if im is None:
            continue
        h, w = im.shape[:2]
        if max(h, w) > max_dim:
            s = float(max_dim) / max(h, w)
            im = cv2.resize(im, (max(1, int(w * s)), max(1, int(h * s))),
                            interpolation=cv2.INTER_AREA)
        imgs.append(im)
    if not imgs:
        raise RuntimeError("没有可读取的图片")
    # 用中位尺寸做统一画布，避免第一张是异常小图导致全部模糊
    ws = sorted(x.shape[1] for x in imgs)
    hs = sorted(x.shape[0] for x in imgs)
    W = int(statistics.median(ws))
    H = int(statistics.median(hs))
    if W < 16 or H < 16:
        raise RuntimeError("图片尺寸过小")
    out = []
    for im in imgs:
        if im.shape[1] != W or im.shape[0] != H:
            im = cv2.resize(im, (W, H), interpolation=cv2.INTER_AREA)
        out.append(im)
    return out


def _align_translation(imgs):
    """相位相关做平移对齐，适合手持 HDR/降噪；焦点堆栈默认不启用。"""
    if len(imgs) < 2:
        return imgs, 0
    ref = cv2.cvtColor(imgs[0], cv2.COLOR_BGR2GRAY)
    ref = cv2.GaussianBlur(ref, (5, 5), 0)
    aligned = [imgs[0]]
    moved = 0
    for im in imgs[1:]:
        g = cv2.cvtColor(im, cv2.COLOR_BGR2GRAY)
        g = cv2.GaussianBlur(g, (5, 5), 0)
        try:
            shift, response = cv2.phaseCorrelate(np.float32(ref), np.float32(g))
            dx, dy = float(shift[0]), float(shift[1])
            if abs(dx) > 1 or abs(dy) > 1:
                M = np.float32([[1, 0, -dx], [0, 1, -dy]])
                im = cv2.warpAffine(im, M, (im.shape[1], im.shape[0]),
                                    flags=cv2.INTER_LINEAR | cv2.WARP_INVERSE_MAP,
                                    borderMode=cv2.BORDER_REPLICATE)
                moved += 1
        except Exception:
            pass
        aligned.append(im)
    return aligned, moved


def _align_mtb(imgs):
    try:
        align = cv2.createAlignMTB(6, 4, True)
        return align.process(imgs), len(imgs) - 1
    except Exception:
        return _align_translation(imgs)


def _u8_to_float(im):
    return im.astype(np.float32) / 255.0


def _float_to_u8(im):
    return np.clip(im * 255.0 + 0.5, 0, 255).astype(np.uint8)


def _auto_tone(img):
    """轻度局部提亮 + 饱和度保护，避免 HDR 融合后发灰。"""
    try:
        lab = cv2.cvtColor(img, cv2.COLOR_BGR2LAB)
        l, a, b = cv2.split(lab)
        clahe = cv2.createCLAHE(clipLimit=1.6, tileGridSize=(8, 8))
        l = clahe.apply(l)
        out = cv2.cvtColor(cv2.merge((l, a, b)), cv2.COLOR_LAB2BGR)
        # 只对低饱和区域做很轻的饱和度回补，避免皮肤/天空过饱和
        hsv = cv2.cvtColor(out, cv2.COLOR_BGR2HSV).astype(np.float32)
        hsv[:, :, 1] = np.clip(hsv[:, :, 1] * 1.04, 0, 255)
        return cv2.cvtColor(hsv.astype(np.uint8), cv2.COLOR_HSV2BGR)
    except Exception:
        return img


def merge_hdr(imgs, do_align=True, deghost=True, deghost_strength=0.35, auto_tone=True):
    if len(imgs) < 2:
        raise RuntimeError("HDR 至少需要 2 张")
    if do_align:
        imgs, moved = _align_mtb(imgs)
    else:
        moved = 0
    f = [_u8_to_float(x) for x in imgs]
    try:
        mertens = cv2.createMergeMertens(1.0, 1.0, 0.0)
    except TypeError:
        mertens = cv2.createMergeMertens()
    fused = mertens.process(f)
    fused = np.clip(fused, 0.0, 1.0)
    if deghost and len(f) >= 3:
        arr = np.stack(f, axis=0)
        var = np.var(arr, axis=0).mean(axis=2)
        # 以中位曝光作为运动区域的“安全底图”
        lumas = [float(x.mean()) for x in f]
        mid = int(np.argmin([abs(x - np.median(lumas)) for x in lumas]))
        mask = np.clip(var / 0.06, 0, 1)[:, :, None] * float(deghost_strength)
        fused = fused * (1.0 - mask) + f[mid] * mask
    out = _float_to_u8(fused)
    if auto_tone:
        out = _auto_tone(out)
    return out, {"aligned": moved, "deghost": bool(deghost and len(imgs) >= 3)}


def merge_focus(imgs, do_align=False, auto_tone=False):
    if len(imgs) < 2:
        raise RuntimeError("焦点堆栈至少需要 2 张")
    if do_align:
        imgs, _ = _align_translation(imgs)
    maps = []
    for im in imgs:
        gray = cv2.cvtColor(im, cv2.COLOR_BGR2GRAY)
        lap = np.abs(cv2.Laplacian(gray, cv2.CV_32F))
        # 小结构增强权重，防止大片纯色区域抢权重
        w = cv2.GaussianBlur(lap, (0, 0), max(1.0, min(gray.shape) / 250.0))
        w = w + 0.02
        maps.append(w)
    stack = np.stack(maps, axis=0)
    denom = np.maximum(stack.sum(axis=0), 1e-6)
    weights = stack / denom
    out = np.zeros_like(imgs[0], dtype=np.float32)
    for im, w in zip(imgs, weights):
        out += im.astype(np.float32) * w[:, :, None]
    out = np.clip(out, 0, 255).astype(np.uint8)
    if auto_tone:
        out = _auto_tone(out)
    return out, {"aligned": 0, "method": "laplacian-focus"}


def merge_noise(imgs, do_align=True, auto_tone=True):
    if len(imgs) < 2:
        raise RuntimeError("降噪堆栈至少需要 2 张")
    if do_align:
        imgs, moved = _align_translation(imgs)
    else:
        moved = 0
    arr = np.stack(imgs, axis=0).astype(np.float32)
    if len(imgs) >= 3:
        out = np.median(arr, axis=0)
    else:
        out = np.mean(arr, axis=0)
    out = np.clip(out, 0, 255).astype(np.uint8)
    if auto_tone:
        out = _auto_tone(out)
    return out, {"aligned": moved, "method": "median" if len(imgs) >= 3 else "mean"}


def do_merge(cfg):
    paths = cfg.get("paths") or []
    if isinstance(paths, str):
        paths = [paths]
    if len(paths) < 1:
        raise RuntimeError("没有输入图片")
    mode = (cfg.get("mode") or "auto").lower()
    if mode == "auto":
        a = analyze(paths)
        if not a["groups"]:
            mode = "hdr" if len(paths) >= 2 else "single"
        else:
            mode = a["groups"][0]["suggested_mode"]
    imgs = _load_stack(paths, int(cfg.get("max_dim") or MAX_DIM))
    if mode == "focus":
        out, meta = merge_focus(imgs, bool(cfg.get("align")), bool(cfg.get("auto_tone")))
    elif mode == "noise":
        out, meta = merge_noise(imgs, cfg.get("align", True) is not False, bool(cfg.get("auto_tone", True)))
    elif mode == "hdr":
        out, meta = merge_hdr(imgs, cfg.get("align", True) is not False,
                              cfg.get("deghost", True) is not False,
                              float(cfg.get("deghost_strength", 0.35)),
                              bool(cfg.get("auto_tone", True)))
    else:
        out = imgs[0]
        meta = {"method": "single"}
    return out, mode, meta


def main():
    if len(sys.argv) < 4:
        print(json.dumps({"ok": False, "error": "usage: hdr_stack.py analyze|merge input output"}))
        return 2
    cmd, inp, out = sys.argv[1].lower(), sys.argv[2], sys.argv[3]
    json_out = out
    try:
        if cmd == "analyze":
            cfg = _json_file(inp)
            result = analyze(cfg.get("paths") or [], int(cfg.get("gap", 90)))
            if "output_json" in cfg:
                out = cfg["output_json"]
            _write_json(out, result)
            print(json.dumps({"ok": True, "groups": len(result["groups"]), "count": result["count"]}, ensure_ascii=False))
            return 0
        if cmd == "merge":
            cfg = _json_file(inp)
            json_out = out
            img, mode, meta = do_merge(cfg)
            out = cfg.get("output") or out
            out_dir = os.path.dirname(out)
            if out_dir:
                os.makedirs(out_dir, exist_ok=True)
            ok = imwrite_u(out, img, [cv2.IMWRITE_JPEG_QUALITY, 96] if out.lower().endswith((".jpg", ".jpeg")) else None)
            if not ok:
                raise RuntimeError("写入失败")
            result = {
                "ok": True, "output": out, "mode": mode,
                "w": int(img.shape[1]), "h": int(img.shape[0]),
                "meta": meta,
            }
            _write_json(json_out, result)
            print(json.dumps(result, ensure_ascii=False))
            return 0
        print(json.dumps({"ok": False, "error": "unknown command"}, ensure_ascii=False))
        return 2
    except Exception as e:
        import traceback
        err_json = {"ok": False, "error": str(e), "tb": traceback.format_exc()[-800:]}
        try:
            _write_json(json_out, err_json)
        except Exception:
            pass
        print(json.dumps(err_json, ensure_ascii=False))
        return 1


if __name__ == "__main__":
    sys.exit(main())
