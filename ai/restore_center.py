#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""AI 修复中心：批量人脸修复、超分、降噪、划痕/污点修复、自动影调。

只复用安装包内已有模型：
- GFPGAN v1.4（beauty.py）
- Swin2SR x4（sr.py）
- YuNet 人脸检测（beauty.py）
不新增云端依赖，不上传照片。
"""
import json
import os
import subprocess
import sys
import time
import traceback

import cv2
import numpy as np

BASE = os.path.dirname(os.path.abspath(__file__))
if BASE not in sys.path:
    sys.path.insert(0, BASE)

from imgio import imread_u, imwrite_u
import beauty

PY = sys.executable
SR_SCRIPT = os.path.join(BASE, "sr.py")


def _tmp(name):
    d = os.path.join(os.environ.get("TEMP", BASE), "ibt_restore")
    os.makedirs(d, exist_ok=True)
    return os.path.join(d, name + "_%d.png" % int(time.time() * 1000))


def _denoise(img, amount):
    if amount <= 0:
        return img
    h = max(1, int(amount))
    # fastNlMeans 的 h 对彩色图很敏感：12~35 比较自然，超过 50 会糊
    return cv2.fastNlMeansDenoisingColored(img, None, h, h, 7, 21)


def _scratch_repair(img, amount):
    if amount <= 0:
        return img
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    med = cv2.medianBlur(gray, 5)
    diff = cv2.absdiff(gray, med)
    th = max(12, int(42 - amount * 0.22))
    mask = (diff > th).astype(np.uint8) * 255
    # 只修复小面积高对比污点/划痕，避免把眼睛、树枝、文字一起抹掉
    mask = cv2.morphologyEx(mask, cv2.MORPH_OPEN, np.ones((2, 2), np.uint8))
    mask = cv2.dilate(mask, np.ones((3, 3), np.uint8), iterations=1)
    if int(mask.sum()) == 0:
        return img
    return cv2.inpaint(img, mask, 3, cv2.INPAINT_TELEA)


def _auto_tone(img):
    lab = cv2.cvtColor(img, cv2.COLOR_BGR2LAB)
    l, a, b = cv2.split(lab)
    clahe = cv2.createCLAHE(clipLimit=1.7, tileGridSize=(8, 8))
    l = clahe.apply(l)
    out = cv2.cvtColor(cv2.merge((l, a, b)), cv2.COLOR_LAB2BGR)
    hsv = cv2.cvtColor(out, cv2.COLOR_BGR2HSV).astype(np.float32)
    hsv[:, :, 1] = np.clip(hsv[:, :, 1] * 1.05, 0, 255)
    return cv2.cvtColor(hsv.astype(np.uint8), cv2.COLOR_HSV2BGR)


def _superres_file(src, scale):
    out = _tmp("sr")
    p = subprocess.run([PY, SR_SCRIPT, src, out, str(int(scale))],
                       cwd=BASE, capture_output=True, text=True,
                       encoding="utf-8", errors="replace", timeout=900)
    if p.returncode != 0 or not os.path.exists(out):
        raise RuntimeError("超分失败: " + (p.stdout or "") + (p.stderr or ""))
    return out


def process_one(path, out_path, opt):
    t0 = time.time()
    img = imread_u(path, cv2.IMREAD_COLOR)
    if img is None:
        return {"ok": False, "input": path, "error": "无法读取图片"}
    work = img.copy()
    actions = []

    # 1) 划痕/污点修复先做，避免后续 AI 把缺陷当成纹理强化
    scratch = int(opt.get("scratch", 0))
    if scratch > 0:
        work = _scratch_repair(work, scratch)
        actions.append("scratch")

    # 2) 降噪
    denoise = int(opt.get("denoise", 0))
    if denoise > 0:
        work = _denoise(work, denoise)
        actions.append("denoise")

    # 3) 人脸修复：复用 beauty.py 的 GFPGAN 合成逻辑，只处理检测到的人脸
    face = int(opt.get("face", 1))
    if face and beauty.detect_faces(work):
        tmp_in = _tmp("face_in")
        tmp_out = _tmp("face_out")
        cv2.imwrite(tmp_in, work)
        try:
            r = beauty.process_image(tmp_in, tmp_out, float(opt.get("face_strength", 0.7)))
            if r.get("ok") and os.path.exists(tmp_out):
                work = imread_u(tmp_out, cv2.IMREAD_COLOR)
                actions.append("gfpgan")
        finally:
            for p in (tmp_in, tmp_out):
                try:
                    if p and os.path.exists(p):
                        os.remove(p)
                except Exception:
                    pass

    # 4) 超分（可选，放在修复之后保持人脸细节）
    scale = int(opt.get("superres", 0))
    if scale in (2, 4):
        src = _tmp("sr_in")
        cv2.imwrite(src, work)
        sr_path = _superres_file(src, scale)
        try:
            work = imread_u(sr_path, cv2.IMREAD_COLOR)
        finally:
            for p in (src, sr_path):
                try:
                    if p and os.path.exists(p):
                        os.remove(p)
                except Exception:
                    pass
        actions.append("sr%d" % scale)

    # 5) 自动影调
    if int(opt.get("auto_tone", 1)):
        work = _auto_tone(work)
        actions.append("tone")

    # 6) 输出
    os.makedirs(os.path.dirname(out_path) or ".", exist_ok=True)
    q = int(opt.get("quality", 95))
    ok = imwrite_u(out_path, work, [cv2.IMWRITE_JPEG_QUALITY, q] if out_path.lower().endswith((".jpg", ".jpeg")) else None)
    if not ok:
        return {"ok": False, "input": path, "error": "写入失败"}
    return {
        "ok": True, "input": path, "output": out_path,
        "actions": actions, "ms": int((time.time() - t0) * 1000),
        "w": int(work.shape[1]), "h": int(work.shape[0]),
    }


def batch(cfg):
    paths = cfg.get("paths") or []
    if isinstance(paths, str):
        paths = [paths]
    if not paths:
        raise RuntimeError("没有输入图片")
    outdir = cfg.get("outdir") or os.path.join(os.path.dirname(paths[0]), "_restored")
    os.makedirs(outdir, exist_ok=True)
    suffix = cfg.get("suffix") or "_restored"
    opt = {
        "face": cfg.get("face", 1), "face_strength": cfg.get("face_strength", 0.7),
        "superres": cfg.get("superres", 0), "denoise": cfg.get("denoise", 0),
        "scratch": cfg.get("scratch", 0), "auto_tone": cfg.get("auto_tone", 1),
        "quality": cfg.get("quality", 95),
    }
    results = []
    for p in paths:
        stem, ext = os.path.splitext(os.path.basename(p))
        ext = ext.lower()
        if ext not in (".jpg", ".jpeg", ".png", ".webp", ".tif", ".tiff", ".bmp"):
            ext = ".jpg"
        dst = os.path.join(outdir, stem + suffix + ext)
        try:
            results.append(process_one(p, dst, opt))
        except Exception as e:
            results.append({"ok": False, "input": p, "error": str(e),
                            "tb": traceback.format_exc()[-500:]})
    okn = sum(1 for x in results if x.get("ok"))
    return {"ok": okn > 0, "count": len(results), "ok_count": okn,
            "failed": len(results) - okn, "outdir": outdir, "results": results}


def main():
    if len(sys.argv) < 4:
        print(json.dumps({"ok": False, "error": "usage: restore_center.py batch input.json output.json"}))
        return 2
    cmd, inp, outp = sys.argv[1].lower(), sys.argv[2], sys.argv[3]
    try:
        cfg = json.load(open(inp, "r", encoding="utf-8"))
        if cmd == "batch":
            r = batch(cfg)
        else:
            raise RuntimeError("unknown command")
        r["output_json"] = outp
        json.dump(r, open(outp, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
        print(json.dumps({"ok": r.get("ok"), "count": r.get("count"),
                          "ok_count": r.get("ok_count"), "failed": r.get("failed")},
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
