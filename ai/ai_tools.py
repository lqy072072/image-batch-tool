#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""v2.30 AI 工具箱本地执行器。

只处理用户自己的照片，所有输出写入独立文件，不覆盖原图。
用法：
  python ai_tools.py run <input.json> <output.json>

支持 op：
  a1_bg      换背景 / 换天空
  a2_retouch 牙齿美白 / 去油光 / 去红眼 / 去痘
  a3_privacy 人脸 / 车牌打码
  a4_crop    智能构图裁切
  a5_score   画质评分
  a8_clarity AI 清晰度增强（超分 + 自定义分辨率 + 锐度）
  a6_search  在当前列表里按文字找图（CNClip）
  b1_matte   透明底抠图
  b4_restore 老照片修复 / 划痕修复
  b6_relight 重新打光
"""
import json
import math
import os
import re
import sys
import time

# 只保留错误日志，避免 ONNX Runtime 的初始化警告写满 C# 的 stderr 管道。
os.environ.setdefault("ORT_LOGGING_LEVEL", "3")

import cv2
import numpy as np

BASE = os.path.dirname(os.path.abspath(__file__))


def _model_dir(name):
    """编辑器模块优先用自己的模型；缺 CNClip/OCR 时复用同装目录下相册模块的模型。"""
    own = os.path.join(BASE, "models", name)
    if os.path.isdir(own):
        return own
    alt = os.path.normpath(os.path.join(BASE, "..", "..", "相册", "ai", "models", name))
    return alt if os.path.isdir(alt) else own
if BASE not in sys.path:
    sys.path.insert(0, BASE)

from imgio import imread_u, imwrite_u  # noqa: E402


def _out(ok, **kw):
    r = {"ok": bool(ok)}
    r.update(kw)
    return r


def _read(path):
    return imread_u(path, cv2.IMREAD_COLOR)


def _write(path, img, params=None):
    os.makedirs(os.path.dirname(os.path.abspath(path)) or ".", exist_ok=True)
    return imwrite_u(path, img, params)


def _hex_bgr(s, fallback=(232, 238, 245)):
    try:
        s = str(s or "").strip().lstrip("#")
        if len(s) == 3:
            s = "".join(c * 2 for c in s)
        if len(s) != 6:
            return fallback
        r, g, b = int(s[0:2], 16), int(s[2:4], 16), int(s[4:6], 16)
        return (b, g, r)
    except Exception:
        return fallback


def _feather_mask(mask, sigma):
    m = (mask > 127).astype(np.float32)
    if sigma and sigma > 0:
        m = cv2.GaussianBlur(m, (0, 0), sigmaX=max(0.5, float(sigma)))
    return np.clip(m, 0, 1)


def _largest_component(mask):
    m = (mask > 0).astype(np.uint8)
    n, lb, stats, _ = cv2.connectedComponentsWithStats(m, 8)
    if n > 1:
        idx = 1 + int(np.argmax(stats[1:, cv2.CC_STAT_AREA]))
        m = np.where(lb == idx, 255, 0).astype(np.uint8)
    return m


def _face_list(img):
    try:
        import beauty_pro as bp
        return bp.detect_faces(img)
    except Exception:
        return []


def _grabcut_mask(img):
    h, w = img.shape[:2]
    mask = np.zeros((h, w), np.uint8)
    bgd, fgd = np.zeros((1, 65), np.float64), np.zeros((1, 65), np.float64)
    rect = (max(1, int(w * 0.035)), max(1, int(h * 0.035)),
            max(2, int(w * 0.93)), max(2, int(h * 0.93)))
    try:
        cv2.grabCut(img, mask, rect, bgd, fgd, 3, cv2.GC_INIT_WITH_RECT)
        m = np.where((mask == cv2.GC_FGD) | (mask == cv2.GC_PR_FGD), 255, 0).astype(np.uint8)
    except Exception:
        m = np.zeros((h, w), np.uint8)
        m[rect[1]:rect[1] + rect[3], rect[0]:rect[0] + rect[2]] = 255
    k = max(3, int(min(h, w) * 0.012))
    m = cv2.morphologyEx(m, cv2.MORPH_CLOSE, np.ones((k, k), np.uint8))
    m = cv2.morphologyEx(m, cv2.MORPH_OPEN, np.ones((max(3, k // 2), max(3, k // 2)), np.uint8))
    m = _largest_component(m)
    # 填掉主体内部的小洞，避免衣服/脸部漏出原背景
    contours, _ = cv2.findContours(m, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    filled = np.zeros_like(m)
    cv2.drawContours(filled, contours, -1, 255, -1)
    return filled


def person_mask(img):
    """人像优先用 BiSeNet 解析；全身/多人/无脸时退回 GrabCut。"""
    h, w = img.shape[:2]
    faces = _face_list(img)
    face = None
    if faces:
        f = sorted(faces, key=lambda x: -x[2])[0]
        if f[2] >= min(w, h) * 0.12:
            face = (float(f[0]), float(f[1]), float(f[2]))
    if face is not None:
        try:
            import idphoto
            m = idphoto.person_mask(img, face)
            if m is not None:
                ratio = float((m > 127).mean())
                if 0.03 < ratio < 0.985:
                    k = max(3, int(min(h, w) * 0.008))
                    m = cv2.morphologyEx(m, cv2.MORPH_CLOSE, np.ones((k, k), np.uint8))
                    m = _largest_component(m)
                    return m
        except Exception:
            pass
    return _grabcut_mask(img)


def _cover_resize(img, w, h):
    ih, iw = img.shape[:2]
    s = max(w / float(iw), h / float(ih))
    nw, nh = max(w, int(math.ceil(iw * s))), max(h, int(math.ceil(ih * s)))
    r = cv2.resize(img, (nw, nh), interpolation=cv2.INTER_LANCZOS4)
    x0, y0 = max(0, (nw - w) // 2), max(0, (nh - h) // 2)
    return r[y0:y0 + h, x0:x0 + w]


def _gradient_bg(w, h, top, bottom):
    t, b = np.array(top, np.float32), np.array(bottom, np.float32)
    rows = np.linspace(0, 1, h, dtype=np.float32)[:, None, None]
    return np.clip(t[None, None, :] * (1 - rows) + b[None, None, :] * rows, 0, 255).astype(np.uint8)


def _sky_layer(w, h):
    top = np.array([210, 150, 92], np.float32)      # BGR 蓝
    mid = np.array([245, 210, 170], np.float32)    # 近地平线暖白
    rows = np.linspace(0, 1, h, dtype=np.float32)[:, None, None]
    grad = top[None, None, :] * (1 - rows) + mid[None, None, :] * rows
    # 低频云层：用固定正弦叠加，避免依赖 numpy.random（便携运行库可能被裁剪）
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    cloud = (0.50 + 0.22 * np.sin(xx / max(8.0, w / 9.0) + yy / max(10.0, h / 14.0))
             + 0.16 * np.sin(xx / max(6.0, w / 23.0) - yy / max(8.0, h / 37.0)))
    cloud = cv2.GaussianBlur(np.clip(cloud, 0, 1), (0, 0), max(6, min(w, h) * 0.02))
    cloud = np.clip((cloud - 0.45) * 1.8, 0, 1)[:, :, None]
    grad = grad + cloud * 55.0
    return np.clip(grad, 0, 255).astype(np.uint8)


def _sky_mask(img):
    h, w = img.shape[:2]
    b, g, r = img[:, :, 0].astype(np.float32), img[:, :, 1].astype(np.float32), img[:, :, 2].astype(np.float32)
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    tex = cv2.Sobel(gray, cv2.CV_32F, 1, 0, ksize=3) ** 2 + cv2.Sobel(gray, cv2.CV_32F, 0, 1, ksize=3) ** 2
    tex = cv2.GaussianBlur(tex, (0, 0), 5)
    upper = np.zeros((h, w), np.float32)
    upper[:max(2, int(h * 0.58)), :] = 1
    color_ok = ((b > r + 8) & (b > 105)) | ((b > 175) & (gray > 150))
    calm = tex < np.percentile(tex, 45)
    m = (upper * color_ok * calm).astype(np.uint8) * 255
    m = cv2.morphologyEx(m, cv2.MORPH_CLOSE, np.ones((max(3, w // 60), max(3, w // 60)), np.uint8))
    m = cv2.GaussianBlur(m.astype(np.float32), (0, 0), max(2, min(w, h) * 0.01))
    return np.clip(m, 0, 255).astype(np.uint8)


def a1_bg(cfg):
    src, dst = cfg.get("input", ""), cfg.get("output", "")
    img = _read(src)
    if img is None:
        return _out(False, error="无法读取输入图片")
    h, w = img.shape[:2]
    mode = str(cfg.get("bg_mode") or "blur").lower()
    color = _hex_bgr(cfg.get("bg_color"), (232, 238, 245))

    if mode == "sky":
        sky_m = _sky_mask(img)
        if float((sky_m > 127).mean()) < 0.06:
            # 没有明显天空：对非主体区域整体换天空
            fm = _feather_mask(person_mask(img), max(1.2, min(w, h) * 0.004))
            sky_m = ((1.0 - fm) * 255).astype(np.uint8)
        layer = _sky_layer(w, h)
        a = _feather_mask(sky_m, max(1.2, min(w, h) * 0.005))[:, :, None]
        out = img.astype(np.float32) * (1 - a) + layer.astype(np.float32) * a
        out = np.clip(out, 0, 255).astype(np.uint8)
        info = "天空替换"
    else:
        m = person_mask(img)
        a = _feather_mask(m, max(1.2, min(w, h) * 0.004))[:, :, None]
        if mode == "color":
            layer = np.zeros((h, w, 3), np.uint8)
            layer[:] = color
            info = "纯色背景"
        elif mode == "gradient":
            c2 = _hex_bgr(cfg.get("bg_color2"), (198, 214, 232))
            layer = _gradient_bg(w, h, color, c2)
            info = "渐变背景"
        elif mode == "image":
            bgp = cfg.get("bg_image") or ""
            bg = _read(bgp) if bgp else None
            if bg is None:
                return _out(False, error="没有读到背景图片")
            layer = _cover_resize(bg, w, h)
            info = "图片背景"
        else:
            layer = cv2.GaussianBlur(img, (0, 0), max(8, min(w, h) * 0.022))
            layer = np.clip(layer.astype(np.float32) * 0.92, 0, 255).astype(np.uint8)
            info = "背景虚化"
        out = img.astype(np.float32) * a + layer.astype(np.float32) * (1 - a)
        out = np.clip(out, 0, 255).astype(np.uint8)

    if not _write(dst, out):
        return _out(False, error="无法写入结果")
    return _out(True, output=os.path.abspath(dst), width=w, height=h,
                op="a1", info=info, engine="BiSeNet + GrabCut")


def _face_crop_rect(face, w, h, scale=1.62):
    cx, cy, fw = face[0], face[1], face[2]
    side = max(96.0, fw * scale)
    x1 = int(round(cx - side / 2.0))
    y1 = int(round(cy - side * 0.58))
    x2 = int(round(cx + side / 2.0))
    y2 = int(round(cy + side * 0.92))
    x1, y1 = max(0, x1), max(0, y1)
    x2, y2 = min(w, x2), min(h, y2)
    if x2 - x1 < 40 or y2 - y1 < 40:
        return None
    return x1, y1, x2, y2


def a2_retouch(cfg):
    src, dst = cfg.get("input", ""), cfg.get("output", "")
    img = _read(src)
    if img is None:
        return _out(False, error="无法读取输入图片")
    strength = max(0.05, min(1.0, float(cfg.get("strength", 0.7))))
    do_teeth = bool(cfg.get("teeth", True))
    do_shine = bool(cfg.get("oil", True))
    do_redeye = bool(cfg.get("redeye", True))
    do_spot = bool(cfg.get("blemish", True))
    faces = _face_list(img)
    if not faces:
        return _out(False, error="没有检测到人脸；这张图不适合做面部局部精修")
    try:
        import beauty_pro as bp
        parser = bp.FaceParser()
    except Exception as ex:
        return _out(False, error="美颜模型未安装：" + str(ex))

    h, w = img.shape[:2]
    work = img.copy()
    changed = 0
    for face in sorted(faces, key=lambda x: -x[2])[:4]:
        rect = _face_crop_rect(face, w, h)
        if not rect:
            continue
        x1, y1, x2, y2 = rect
        crop = work[y1:y2, x1:x2].copy()
        labels = parser.parse(crop)
        skin = ((labels == 1) | (labels == 10)).astype(np.float32)
        eyes = np.isin(labels, [4, 5]).astype(np.float32)
        brows = np.isin(labels, [2, 3]).astype(np.float32)
        lips = np.isin(labels, [9, 10, 11, 12, 13]).astype(np.float32)
        protect = cv2.dilate(np.clip(eyes + brows + lips, 0, 1), np.ones((7, 7), np.uint8))
        if do_teeth:
            crop = bp.op_teeth(crop, labels, strength)
            changed += 1
        if do_shine:
            crop = bp.op_shine(crop, skin, strength)
            changed += 1
        if do_spot:
            crop = bp.op_spot(crop, skin, strength, protect)
            changed += 1
        if do_redeye:
            b, g, r = crop[:, :, 0].astype(np.float32), crop[:, :, 1].astype(np.float32), crop[:, :, 2].astype(np.float32)
            red = ((r > g * 1.28) & (r > b * 1.28) & (eyes > 0.5)).astype(np.uint8)
            red = cv2.dilate(red, np.ones((5, 5), np.uint8))
            if int(red.sum()) > 30:
                gray = cv2.cvtColor(crop, cv2.COLOR_BGR2GRAY)
                g3 = cv2.cvtColor(gray, cv2.COLOR_GRAY2BGR).astype(np.float32)
                a = cv2.GaussianBlur(red.astype(np.float32), (0, 0), 2.0)[:, :, None] * strength
                crop = np.clip(crop.astype(np.float32) * (1 - a) + g3 * a, 0, 255).astype(np.uint8)
                changed += 1
        # 矩形羽化贴回，避免明显接缝
        m = np.zeros((y2 - y1, x2 - x1), np.float32)
        pad = max(4, min(m.shape) // 14)
        m[pad:m.shape[0] - pad, pad:m.shape[1] - pad] = 1.0
        m = cv2.GaussianBlur(m, (0, 0), max(3, pad * 0.8))[:, :, None]
        work[y1:y2, x1:x2] = np.clip(crop.astype(np.float32) * m + work[y1:y2, x1:x2].astype(np.float32) * (1 - m), 0, 255).astype(np.uint8)

    if not _write(dst, work):
        return _out(False, error="无法写入结果")
    return _out(True, output=os.path.abspath(dst), width=w, height=h,
                op="a2", face_count=len(faces), info="牙齿/油光/红眼/痘印局部精修", engine="BiSeNet")


def _blur_region(img, x1, y1, x2, y2, style):
    x1, y1 = max(0, x1), max(0, y1)
    x2, y2 = min(img.shape[1], x2), min(img.shape[0], y2)
    if x2 <= x1 or y2 <= y1:
        return
    roi = img[y1:y2, x1:x2]
    if style == "mosaic":
        k = max(3, int(min(roi.shape[:2]) * 0.06))
        small = cv2.resize(roi, (max(1, roi.shape[1] // k), max(1, roi.shape[0] // k)), interpolation=cv2.INTER_LINEAR)
        roi[:] = cv2.resize(small, (roi.shape[1], roi.shape[0]), interpolation=cv2.INTER_NEAREST)
    else:
        sigma = max(5, int(min(roi.shape[:2]) * 0.11))
        roi[:] = cv2.GaussianBlur(roi, (0, 0), sigmaX=sigma)


def _plate_boxes(img):
    try:
        from ocr_common import OcrEngine, _detect_boxes, _crop_box
    except Exception:
        return []
    model_dir = _model_dir("ocr")
    if not os.path.isdir(model_dir):
        return []
    eng = OcrEngine(model_dir, gpu=False, det_limit=1280, threads=2)
    if not eng.ok:
        return []
    try:
        x, sx, sy = eng._det_preprocess(img)
        prob = eng.det.run(None, {"x": x})[0][0, 0]
        boxes = _detect_boxes(prob, sx, sy, thresh=0.26, box_thresh=0.42)
        out = []
        for box, _score in boxes:
            crop = _crop_box(img, box)
            if crop is None:
                continue
            text, conf = eng._recognize_crop(crop)
            t = (text or "").replace(" ", "").upper()
            if conf < 0.45 or len(t) < 5:
                continue
            if re.search(r"[京津沪渝冀豫云辽黑湘皖鲁新苏浙赣鄂桂甘晋蒙陕吉闽贵粤青藏川宁琼][A-Z][A-Z0-9]{4,6}", t):
                xs, ys = box[:, 0], box[:, 1]
                out.append((int(xs.min()), int(ys.min()), int(xs.max()), int(ys.max())))
                continue
            if re.search(r"[A-Z]{2,4}[0-9]{3,5}", t) and 5 <= len(t) <= 9:
                xs, ys = box[:, 0], box[:, 1]
                out.append((int(xs.min()), int(ys.min()), int(xs.max()), int(ys.max())))
        return out
    except Exception:
        return []


def a3_privacy(cfg):
    src, dst = cfg.get("input", ""), cfg.get("output", "")
    img = _read(src)
    if img is None:
        return _out(False, error="无法读取输入图片")
    h, w = img.shape[:2]
    style = str(cfg.get("style") or "blur").lower()
    do_face = bool(cfg.get("face", True))
    do_plate = bool(cfg.get("plate", True))
    boxes = []
    if do_face:
        for f in _face_list(img):
            cx, cy, fw = f[0], f[1], f[2]
            fh = fw * 1.32
            boxes.append((int(cx - fw * 0.72), int(cy - fh * 0.66),
                          int(cx + fw * 0.72), int(cy + fh * 0.72), "face"))
    if do_plate:
        for b in _plate_boxes(img):
            boxes.append((b[0], b[1], b[2], b[3], "plate"))
    if not boxes:
        return _out(False, error="没有检测到人脸或车牌；可只保留一项打码")
    work = img.copy()
    for x1, y1, x2, y2, _typ in boxes:
        _blur_region(work, x1, y1, x2, y2, style)
    if not _write(dst, work):
        return _out(False, error="无法写入结果")
    return _out(True, output=os.path.abspath(dst), width=w, height=h,
                op="a3", boxes=len(boxes), info="人脸/车牌已打码", engine="YuNet + PP-OCR")


def _saliency_center(img):
    h, w = img.shape[:2]
    small = cv2.resize(img, (min(640, w), max(1, int(min(640, w) * h / max(1, w)))), interpolation=cv2.INTER_AREA)
    gray = cv2.cvtColor(small, cv2.COLOR_BGR2GRAY)
    grad = cv2.Sobel(gray, cv2.CV_32F, 1, 0, ksize=3) ** 2 + cv2.Sobel(gray, cv2.CV_32F, 0, 1, ksize=3) ** 2
    grad = cv2.GaussianBlur(grad, (0, 0), 12)
    # 中心先验 + 饱和度，避免大面积天空/墙面把主体拉偏
    hh, ww = grad.shape[:2]
    yy, xx = np.mgrid[0:hh, 0:ww]
    center = np.exp(-(((xx - ww / 2) / (ww * 0.55)) ** 2 + ((yy - hh / 2) / (hh * 0.55)) ** 2))
    sat = cv2.cvtColor(small, cv2.COLOR_BGR2HSV)[:, :, 1].astype(np.float32) / 255.0
    score = grad * (0.55 + 0.45 * sat) * center
    score = cv2.GaussianBlur(score, (0, 0), 6)
    x = float((score * xx).sum() / max(1e-6, score.sum())) / ww
    y = float((score * yy).sum() / max(1e-6, score.sum())) / hh
    return x, y


def a4_crop(cfg):
    src, dst = cfg.get("input", ""), cfg.get("output", "")
    img = _read(src)
    if img is None:
        return _out(False, error="无法读取输入图片")
    h, w = img.shape[:2]
    ratio_s = str(cfg.get("ratio") or "1:1")
    try:
        rw, rh = [float(x) for x in ratio_s.split(":")]
        ratio = rw / max(1e-6, rh)
    except Exception:
        ratio = 1.0
    faces = _face_list(img)
    if faces:
        f = sorted(faces, key=lambda x: -x[2])[0]
        cx, cy, fw = f[0], f[1], f[2]
        crop_h = min(h, max(fw * 4.6, w / max(1.0, ratio) * 0.45))
        if ratio >= 1.0:
            # 横构图必须同时受画布宽度约束，否则会退化成整张不裁
            crop_h = min(crop_h, w / max(1e-6, ratio))
        crop_w = min(w, crop_h * ratio)
        # 人像：头顶留 12%，脸中心偏上 40%
        y0 = cy - crop_h * 0.38
        x0 = cx - crop_w * 0.5
        info = "人脸构图"
    else:
        sx, sy = _saliency_center(img)
        crop_h = min(h, w / ratio) if ratio >= 1 else h
        crop_w = crop_h * ratio
        if crop_w > w:
            crop_w = w
            crop_h = crop_w / ratio
        x0 = sx * w - crop_w * 0.5
        y0 = sy * h - crop_h * 0.45
        info = "主体显著度构图"
    x0 = int(round(max(0, min(w - crop_w, x0))))
    y0 = int(round(max(0, min(h - crop_h, y0))))
    cw, ch = int(round(crop_w)), int(round(crop_h))
    out = img[y0:y0 + ch, x0:x0 + cw].copy()
    if not _write(dst, out):
        return _out(False, error="无法写入结果")
    return _out(True, output=os.path.abspath(dst), width=int(out.shape[1]), height=int(out.shape[0]),
                op="a4", info=info, engine="YuNet + 显著度")


def _eye_aspect(lm, idx):
    p = lm[idx]
    v1 = np.linalg.norm(p[1] - p[5])
    v2 = np.linalg.norm(p[2] - p[4])
    h = np.linalg.norm(p[0] - p[3]) + 1e-6
    return float((v1 + v2) / (2.0 * h))


def a5_score(cfg):
    src = cfg.get("input", "")
    img = _read(src)
    if img is None:
        return _out(False, error="无法读取输入图片")
    h, w = img.shape[:2]
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    lap = cv2.Laplacian(gray, cv2.CV_64F)
    sharp = float(lap.var())
    sharp_score = max(0.0, min(1.0, math.log10(1.0 + sharp) / 3.2))
    mean = float(gray.mean())
    p1, p99 = np.percentile(gray, [1, 99])
    clip_low = float((gray <= 3).mean())
    clip_high = float((gray >= 252).mean())
    exposure_score = max(0.0, 1.0 - abs(mean - 128.0) / 128.0 - (clip_low + clip_high) * 1.5)
    contrast_score = max(0.0, min(1.0, float(gray.std()) / 62.0))
    hsv = cv2.cvtColor(img, cv2.COLOR_BGR2HSV)
    color_score = max(0.0, min(1.0, float(hsv[:, :, 1].mean()) / 150.0))
    faces = _face_list(img)
    face_score = 1.0 if faces else 0.55
    closed = 0
    if faces:
        try:
            import beauty_pro as bp
            mesh = bp.FaceMesh()
            for f in sorted(faces, key=lambda x: -x[2])[:3]:
                cx, cy, fw = f[0], f[1], f[2]
                box = (int(cx - fw * 0.62), int(cy - fw * 0.72), int(cx + fw * 0.62), int(cy + fw * 0.72))
                lm = mesh.predict(img, box)
                left = _eye_aspect(lm, [33, 160, 158, 133, 153, 144])
                right = _eye_aspect(lm, [362, 385, 387, 263, 373, 380])
                if min(left, right) < 0.16:
                    closed += 1
        except Exception:
            pass
    eye_score = 1.0 if closed == 0 else max(0.2, 1.0 - closed * 0.35)
    score = 100.0 * (0.30 * sharp_score + 0.24 * exposure_score + 0.16 * contrast_score +
                     0.12 * color_score + 0.10 * face_score + 0.08 * eye_score)
    score = int(round(max(0, min(100, score))))
    if score >= 82:
        label = "优秀"
    elif score >= 68:
        label = "良好"
    elif score >= 50:
        label = "可用"
    else:
        label = "建议重拍/重修"
    notes = []
    if sharp_score < 0.45:
        notes.append("清晰度偏低")
    if exposure_score < 0.55:
        notes.append("曝光偏差")
    if clip_high > 0.02:
        notes.append("高光溢出")
    if clip_low > 0.06:
        notes.append("暗部死黑")
    if closed:
        notes.append("疑似闭眼 %d 张脸" % closed)
    return _out(True, op="a5", score=score, label=label, width=w, height=h,
                metrics={"sharpness": round(sharp_score * 100), "exposure": round(exposure_score * 100),
                         "contrast": round(contrast_score * 100), "color": round(color_score * 100),
                         "faces": len(faces), "closed_eyes": closed,
                         "megapixels": round(w * h / 1e6, 1)},
                notes=notes, info="画质评分")


_subpixel_sess = None
_subpixel_path = None


def _subpixel_upscale_y(img):
    """轻量 SubPixelCNN x3：只对亮度分块超分，颜色走双三次，速度快、显存/内存低。"""
    global _subpixel_sess, _subpixel_path
    model = os.path.join(_model_dir(""), "subpixel_x3.onnx")
    if not os.path.isfile(model):
        raise RuntimeError("缺少 subpixel_x3.onnx")
    if _subpixel_sess is None or _subpixel_path != model:
        import onnxruntime as ort
        opts = ort.SessionOptions()
        opts.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        opts.intra_op_num_threads = max(1, (os.cpu_count() or 4) - 1)
        _subpixel_sess = ort.InferenceSession(model, sess_options=opts, providers=["CPUExecutionProvider"])
        _subpixel_path = model

    h, w = img.shape[:2]
    ycc = cv2.cvtColor(img, cv2.COLOR_BGR2YCrCb)
    y_chan = ycc[:, :, 0].astype(np.float32) / 255.0
    tile, overlap = 224, 16
    stride = tile - overlap
    starts_y = [0] if h <= tile else list(range(0, h - tile + 1, stride))
    if h > tile and starts_y[-1] != h - tile:
        starts_y.append(h - tile)
    starts_x = [0] if w <= tile else list(range(0, w - tile + 1, stride))
    if w > tile and starts_x[-1] != w - tile:
        starts_x.append(w - tile)

    out_y = np.zeros((h * 3, w * 3), dtype=np.float32)
    count = np.zeros((h * 3, w * 3), dtype=np.uint8)
    in_name = _subpixel_sess.get_inputs()[0].name
    for sy in starts_y:
        for sx in starts_x:
            patch = y_chan[sy:sy + tile, sx:sx + tile]
            ph, pw = patch.shape[:2]
            if ph < tile or pw < tile:
                patch = cv2.copyMakeBorder(patch, 0, tile - ph, 0, tile - pw, cv2.BORDER_REPLICATE)
            pred = _subpixel_sess.run(None, {in_name: patch[None, None]})[0][0, 0]
            real_scale = pred.shape[0] // tile
            oh, ow = ph * real_scale, pw * real_scale
            oy, ox = sy * real_scale, sx * real_scale
            out_y[oy:oy + oh, ox:ox + ow] += pred[:oh, :ow]
            count[oy:oy + oh, ox:ox + ow] += 1

    count[count == 0] = 1
    y_up = np.clip(out_y / count, 0, 1)
    cr_up = cv2.resize(ycc[:, :, 1], (w * 3, h * 3), interpolation=cv2.INTER_CUBIC)
    cb_up = cv2.resize(ycc[:, :, 2], (w * 3, h * 3), interpolation=cv2.INTER_CUBIC)
    ycc_up = cv2.merge([np.clip(y_up * 255, 0, 255).astype(np.uint8), cr_up, cb_up])
    return cv2.cvtColor(ycc_up, cv2.COLOR_YCrCb2BGR)


def a8_clarity(cfg):
    """AI 清晰度增强：超分补细节 + 可调清晰度 + 可调锐度，支持自定义输出宽高。"""
    src_path = cfg.get("input", "")
    dst_path = cfg.get("output", "")
    img = _read(src_path)
    if img is None:
        return _out(False, error="无法读取输入图片")
    h, w = img.shape[:2]
    mode = str(cfg.get("res_mode") or "2x").lower()
    clarity = max(0, min(100, int(cfg.get("clarity", 65) or 65)))
    sharpness = max(0, min(100, int(cfg.get("sharpness", 35) or 35)))
    lock = bool(cfg.get("lock_ratio", True))
    tw = int(cfg.get("target_w", 0) or 0)
    th = int(cfg.get("target_h", 0) or 0)

    if mode == "4x":
        tw, th = w * 4, h * 4
    elif mode == "2x":
        tw, th = w * 2, h * 2
    else:
        if tw <= 0 and th <= 0:
            tw, th = w * 2, h * 2
        elif tw <= 0:
            tw = int(round(th * w / float(h)))
        elif th <= 0:
            th = int(round(tw * h / float(w)))
        if lock and tw > 0:
            th = int(round(tw * h / float(w)))

    # 防止超大自定义尺寸把内存撑爆；长边上限 8192，保持比例。
    max_side = 8192
    if max(tw, th) > max_side:
        f = max_side / float(max(tw, th))
        tw, th = int(round(tw * f)), int(round(th * f))
    tw = max(64, min(max_side, tw))
    th = max(64, min(max_side, th))

    res = img.copy()
    engine = "local-detail"
    try:
        # 轻量 AI 超分优先：0.02s/224px 分块，普通照片可在数秒内完成；超大图走 Lanczos 防内存峰值。
        if w * h <= 12000000 and os.path.isfile(os.path.join(BASE, "models", "subpixel_x3.onnx")):
            try:
                res = _subpixel_upscale_y(img)
                engine = "SubPixelCNN x3"
            except Exception:
                res = img.copy()

        if res.shape[1] != tw or res.shape[0] != th:
            interp = cv2.INTER_AREA if (res.shape[1] > tw or res.shape[0] > th) else cv2.INTER_LANCZOS4
            res = cv2.resize(res, (tw, th), interpolation=interp)

        if clarity > 0:
            alpha = clarity / 100.0 * 0.62
            enhanced = cv2.detailEnhance(
                res, sigma_s=5.0 + clarity * 0.08, sigma_r=0.08 + clarity * 0.0015
            )
            res = cv2.addWeighted(res, 1.0 - alpha, enhanced, alpha, 0.0)

        if sharpness > 0:
            amount = sharpness / 100.0 * 1.15
            blurred = cv2.GaussianBlur(res, (0, 0), 1.0)
            res = cv2.addWeighted(res, 1.0 + amount, blurred, -amount, 0.0)

        res = np.clip(res, 0, 255).astype(np.uint8)
        _write(dst_path, res)
        return _out(True, op="a8", width=int(res.shape[1]), height=int(res.shape[0]),
                    source_width=w, source_height=h, clarity=clarity, sharpness=sharpness,
                    res_mode=mode, engine=engine,
                    info="AI 超分 + 清晰度/锐度增强")
    except Exception as ex:
        return _out(False, error="清晰度增强失败：" + str(ex)[:180])


def a6_search(cfg):
    query = str(cfg.get("query") or "").strip()
    paths = cfg.get("paths") or []
    if isinstance(paths, str):
        paths = [paths]
    paths = [p for p in paths if p and os.path.isfile(p)]
    if not query:
        return _out(False, error="先输入要找的内容，例如：海边日落 / 穿红衣服的人")
    if not paths:
        return _out(False, error="当前列表没有图片")
    model_dir = _model_dir("cnclip")
    txt_model = os.path.join(model_dir, "cnclip_vitb16.txt.fp32.onnx")
    img_model = os.path.join(model_dir, "cnclip_vitb16.img.fp32.onnx")
    vocab = os.path.join(model_dir, "cn_vocab.txt")
    if not (os.path.isfile(txt_model) and os.path.isfile(img_model) and os.path.isfile(vocab)):
        return _out(False, error="CNClip 语义模型未安装，无法做自然语言找图")
    try:
        from clip_common import set_model_dir, create_session, BertTokenizer, preprocess_image, l2norm
        set_model_dir(model_dir)
        txt_sess, _ = create_session(txt_model, gpu=True, threads=4)
        img_sess, _ = create_session(img_model, gpu=True, threads=4)
        tok = BertTokenizer(vocab)
        ids = np.array([tok.encode(query)], dtype=np.int64)
        q = txt_sess.run(None, {"texts": ids})[0][0]
        q = l2norm(q.reshape(1, -1))[0]
        matches = []
        cap = max(1, min(240, int(cfg.get("max_items", 150))))
        for p in paths[:cap]:
            try:
                x = preprocess_image(p)
                v = img_sess.run(None, {"image": x})[0][0]
                v = l2norm(v.reshape(1, -1))[0]
                score = float(np.dot(q, v))
                matches.append({"path": os.path.abspath(p), "score": round(score, 4)})
            except Exception:
                continue
        matches.sort(key=lambda x: -x["score"])
        return _out(True, op="a6", query=query, matches=matches[:8], scanned=min(len(paths), cap),
                    info="CNClip 语义检索")
    except Exception as ex:
        # 模型推理失败时退回文件名匹配，至少能用
        low = query.lower()
        hits = [{"path": os.path.abspath(p), "score": 0.15} for p in paths if low in os.path.basename(p).lower()]
        if hits:
            return _out(True, op="a6", query=query, matches=hits[:8], scanned=len(paths),
                        info="文件名匹配（语义模型不可用）")
        return _out(False, error="语义检索失败：" + str(ex)[:180])


def b1_matte(cfg):
    src, dst = cfg.get("input", ""), cfg.get("output", "")
    img = _read(src)
    if img is None:
        return _out(False, error="无法读取输入图片")
    h, w = img.shape[:2]
    m = person_mask(img)
    er = max(1, int(min(w, h) * 0.002))
    m = cv2.erode((m > 127).astype(np.uint8), np.ones((er * 2 + 1, er * 2 + 1), np.uint8))
    # 只清掉靠近轮廓边缘的纯背景像素（发丝缝隙、腋下/手臂间透出的背景），保护面部浅色皮肤。
    b = max(4, min(w, h) // 40)
    border = np.concatenate([img[:b].reshape(-1, 3), img[-b:].reshape(-1, 3),
                             img[:, :b].reshape(-1, 3), img[:, -b:].reshape(-1, 3)])
    bg = np.median(border, axis=0)
    diff = np.max(np.abs(img.astype(np.int16) - bg.astype(np.int16)), axis=2)
    sim = (diff < 26).astype(np.uint8)
    alpha0 = _feather_mask(m * 255, max(1.0, min(w, h) * 0.0028))
    near_edge = cv2.dilate((alpha0 < 0.55).astype(np.uint8), np.ones((41, 41), np.uint8))
    m[(sim > 0) & (near_edge > 0)] = 0
    alpha = _feather_mask(m * 255, max(1.0, min(w, h) * 0.0028))
    # 用最近的前景颜色填满透明区的 RGB，避免半透明边缘保留白底，合成时出现白边。
    a01 = np.clip(alpha, 0, 1)
    known = a01 > 0.985
    fill = img.copy()
    fill[~known] = 0
    for _ in range(14):
        dil = cv2.dilate(fill, np.ones((5, 5), np.uint8))
        newly = (~known) & (dil.sum(axis=2) > 0)
        fill[newly] = dil[newly]
        known |= newly
        if known.all():
            break
    bgra = cv2.cvtColor(fill, cv2.COLOR_BGR2BGRA)
    bgra[:, :, 3] = np.clip(alpha * 255.0, 0, 255).astype(np.uint8)
    if not _write(dst, bgra):
        return _out(False, error="无法写入透明 PNG")
    return _out(True, output=os.path.abspath(dst), width=w, height=h,
                op="b1", info="透明底 PNG（发丝边缘已羽化）", engine="BiSeNet + GrabCut")


def b4_restore(cfg):
    src, dst = cfg.get("input", ""), cfg.get("output", "")
    img = _read(src)
    if img is None:
        return _out(False, error="无法读取输入图片")
    mode = str(cfg.get("mode") or "old").lower()
    strength = max(0.2, min(1.0, float(cfg.get("strength", 0.75))))
    try:
        import restore_center
        opt = {"face": 1, "face_strength": strength, "scratch": 0, "denoise": 0, "auto_tone": 1, "quality": 95}
        if mode == "scratch":
            opt["face"] = 1
            opt["scratch"] = int(28 + 52 * strength)
            opt["denoise"] = 8
        elif mode == "old":
            opt["face"] = 1
            opt["scratch"] = int(35 + 45 * strength)
            opt["denoise"] = 12
        else:
            opt["face"] = 0
            opt["auto_tone"] = 1
        r = restore_center.process_one(src, dst, opt)
        if not r.get("ok"):
            return _out(False, error=r.get("error") or "修复失败")
        return _out(True, output=os.path.abspath(dst), width=r.get("w"), height=r.get("h"),
                    op="b4", mode=mode, info="GFPGAN + 划痕修补 + 自动影调", engine="GFPGAN v1.4")
    except Exception as ex:
        return _out(False, error="修复失败：" + str(ex)[:180])


def b6_relight(cfg):
    src, dst = cfg.get("input", ""), cfg.get("output", "")
    img = _read(src)
    if img is None:
        return _out(False, error="无法读取输入图片")
    h, w = img.shape[:2]
    strength = max(0.1, min(1.0, float(cfg.get("strength", 0.55))))
    direction = str(cfg.get("direction") or "left").lower()
    warmth = max(-1.0, min(1.0, float(cfg.get("warmth", 0.15))))
    lab = cv2.cvtColor(img, cv2.COLOR_BGR2LAB).astype(np.float32)
    L, A, B = lab[:, :, 0], lab[:, :, 1], lab[:, :, 2]
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    nx = xx / max(1, w - 1) - 0.5
    ny = yy / max(1, h - 1) - 0.5
    dirs = {
        "left": (-1.0, -0.35), "right": (1.0, -0.35), "top": (0.0, -1.0),
        "back": (0.0, 1.0), "soft": (0.0, 0.0), "front": (0.0, -0.15),
    }
    dx, dy = dirs.get(direction, (-1.0, -0.35))
    shade = -(nx * dx + ny * dy)
    if direction == "soft":
        shade = 1.0 - np.sqrt(nx * nx + ny * ny) * 0.9
    shade = cv2.GaussianBlur(shade.astype(np.float32), (0, 0), max(8, min(w, h) * 0.06))
    # 保中间调：只抬受光面，压暗背光面，避免死黑/死白
    base = cv2.GaussianBlur(L, (0, 0), max(8, min(w, h) * 0.05))
    detail = L - base
    newL = L + shade * (38.0 * strength) + (base - 128.0) * (0.06 * strength)
    newL = np.clip(newL, 0, 255)
    # 保留原图高频细节，防止磨皮感
    newL = np.clip(newL * 0.94 + detail * 0.06, 0, 255)
    newB = np.clip(B + warmth * 9.0, 0, 255)
    newA = np.clip(A + (-warmth) * 2.0, 0, 255)
    out = cv2.cvtColor(np.clip(np.stack([newL, newA, newB], axis=2), 0, 255).astype(np.uint8),
                       cv2.COLOR_LAB2BGR)
    if not _write(dst, out):
        return _out(False, error="无法写入结果")
    return _out(True, output=os.path.abspath(dst), width=w, height=h,
                op="b6", info="方向光重新打光：" + direction, engine="LAB 光照重建")


def b2_mask_quick(cfg):
    """涂抹消除 · 本地快速纹理修复（不依赖大模型）。"""
    src = (cfg.get("input") or "").strip()
    dst = (cfg.get("output") or "").strip()
    mpath = (cfg.get("mask") or "").strip()
    if not os.path.isfile(src):
        return _out(False, error="输入图片不存在：" + src)
    if not mpath or not os.path.isfile(mpath):
        return _out(False, error="没有收到涂抹遮罩。")
    img = _read(src)
    if img is None:
        return _out(False, error="无法读取输入图片。")
    m = imread_u(mpath, cv2.IMREAD_GRAYSCALE)
    if m is None:
        return _out(False, error="无法读取涂抹遮罩。")
    h, w = img.shape[:2]
    if m.shape[:2] != (h, w):
        m = cv2.resize(m, (w, h), interpolation=cv2.INTER_NEAREST)
    mask = (m > 40).astype(np.uint8) * 255
    if not np.any(mask):
        return _out(False, error="涂抹区域为空，请先在图上涂抹要消除的位置。")
    grow = max(2, int(round(min(w, h) * 0.004)))
    mask = cv2.dilate(mask, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (grow * 2 + 1, grow * 2 + 1)))
    radius = max(3, int(round(min(w, h) * 0.012)))
    fixed = cv2.inpaint(img, mask, radius, cv2.INPAINT_TELEA)
    soft = cv2.GaussianBlur(mask.astype(np.float32) / 255.0, (0, 0), max(1.2, min(w, h) * 0.003))[:, :, None]
    out = (img.astype(np.float32) * (1.0 - soft) + fixed.astype(np.float32) * soft).clip(0, 255).astype(np.uint8)
    if not _write(dst, out):
        return _out(False, error="无法写入结果")
    return _out(True, output=os.path.abspath(dst), width=w, height=h,
                op="b2_mask", info="本地纹理消除", engine="OpenCV Telea")


def _trim_dark_border(img):
    """裁掉截图常见的 1–8px 纯黑边，避免扩图把黑边一起放大。"""
    h, w = img.shape[:2]
    max_y, max_x = max(1, int(h * 0.04)), max(1, int(w * 0.04))
    top = bottom = left = right = 0

    def dark_row(y):
        row = img[y, :, :3]
        return float(row.mean()) < 36.0 and float(row.std()) < 12.0

    def dark_col(x):
        col = img[:, x, :3]
        return float(col.mean()) < 36.0 and float(col.std()) < 12.0

    while top < max_y and dark_row(top):
        top += 1
    while bottom < max_y and dark_row(h - 1 - bottom):
        bottom += 1
    while left < max_x and dark_col(left):
        left += 1
    while right < max_x and dark_col(w - 1 - right):
        right += 1
    if not (top or bottom or left or right):
        return img
    y2, x2 = h - bottom, w - right
    if y2 - top < 32 or x2 - left < 32:
        return img
    return img[top:y2, left:x2].copy()


def b3_extend(cfg):
    """智能扩图 · 只延展背景，原图人物逐像素保留。

    不再让扩散模型重画人像：近景人像向下续画时，模型会按另一套比例生成
    腿和身体，出现“大头小身体”。这里只扩大画布，从画面边缘取一条镜像条带，
    重采样后做强景深虚化，再把原图原样贴回中心；人物比例、五官、字幕不变。

    方向：四周 all / 上 top / 下 bottom / 左 left / 右 right。
    向下扩只补背景，不生成身体；原图逐像素保留。
    """
    src = (cfg.get("input") or "").strip()
    dst = (cfg.get("output") or "").strip()
    if not os.path.isfile(src):
        return _out(False, error="输入图片不存在：" + src)
    img = _read(src)
    if img is None:
        return _out(False, error="无法读取输入图片。")
    img = _trim_dark_border(img)
    direction = (cfg.get("direction") or "all").strip().lower()
    try:
        pct = float(cfg.get("expand_percent", 25))
    except Exception:
        pct = 25.0
    pct = max(5.0, min(60.0, pct)) / 100.0
    h0, w0 = img.shape[:2]
    px, py = int(round(w0 * pct)), int(round(h0 * pct))
    if direction == "horizontal":
        pl, pr, pt, pb = px, px, 0, 0
    elif direction == "left":
        pl, pr, pt, pb = px, 0, 0, 0
    elif direction == "right":
        pl, pr, pt, pb = 0, px, 0, 0
    elif direction == "top":
        pl, pr, pt, pb = 0, 0, py, 0
    elif direction == "bottom":
        pl, pr, pt, pb = 0, 0, 0, py
    elif direction == "vertical":
        pl, pr, pt, pb = 0, 0, py, py
    else:
        # 四周：上下左右都扩，原图仍逐像素贴回中心。
        pl, pr, pt, pb = px, px, py, py
    if not (pl or pr or pt or pb):
        return _out(False, error="扩展比例为 0，无法扩图。")

    H, W = h0 + pt + pb, w0 + pl + pr
    # 先判断左右边缘是不是纯色/近似纯色背景：证件照、棚拍、纯墙前的人像，
    # 直接铺边缘主色，避免把“墙”镜像成一条深色带，出现照片贴在灰底上的硬框。
    sw = max(10, min(w0 // 4, 96))
    sh = max(10, min(h0 // 4, 96))
    narrow = max(8, min(w0 // 12, 52))
    sample_h = max(12, int(h0 * 0.45))
    side_l = img[:sample_h, :narrow]
    side_r = img[:sample_h, w0 - narrow:]
    side_gray = cv2.cvtColor(np.concatenate([side_l, side_r], axis=1), cv2.COLOR_BGR2GRAY)
    edge_pct = float(cv2.Canny(side_gray, 40, 100).mean()) / 255.0 * 100.0
    flat_bg = edge_pct < 5.0

    if flat_bg:
        # 纯墙/棚拍：逐行取左右窄边的中位色，保留原背景的上下明暗渐变，
        # 横向铺满新增区。这样不会把浅灰墙镜像成深灰条，也不会出现硬框。
        lp = np.median(img[:, :narrow].astype(np.float32), axis=1)
        rp = np.median(img[:, w0 - narrow:].astype(np.float32), axis=1)
        lp = cv2.GaussianBlur(lp.reshape(h0, 1, 3), (1, 1), sigmaX=0, sigmaY=max(12.0, h0 * 0.05)).reshape(h0, 3)
        rp = cv2.GaussianBlur(rp.reshape(h0, 1, 3), (1, 1), sigmaX=0, sigmaY=max(12.0, h0 * 0.05)).reshape(h0, 3)
        # 把条带整体色偏校正到原图左右边缘的平均色；保留平滑的上下渐变，
        # 不逐行复制边缘噪声，避免侧带出现横向扫描线。
        lp = lp + (img[:, 0].astype(np.float32).mean(axis=0) - lp.mean(axis=0))
        rp = rp + (img[:, w0 - 1].astype(np.float32).mean(axis=0) - rp.mean(axis=0))
        ys = np.clip(np.arange(H) - pt, 0, h0 - 1)
        fill = np.ones((H, W, 3), np.float32) * ((lp.mean(axis=0) + rp.mean(axis=0)) / 2.0)
        if pl:
            t = ((np.arange(pl, dtype=np.float32) + 1.0) / max(1, pl))[None, :, None] ** 3
            edge_l = cv2.GaussianBlur(img[:, 0].astype(np.float32).reshape(h0, 1, 3), (1, 1), sigmaX=0, sigmaY=max(12.0, h0 * 0.05)).reshape(h0, 3)
            fill[:, :pl, :] = lp[ys][:, None, :] * (1.0 - t) + edge_l[ys][:, None, :] * t
        if pr:
            t = ((pr - np.arange(pr, dtype=np.float32)) / max(1, pr))[None, :, None] ** 3
            edge_r = cv2.GaussianBlur(img[:, w0 - 1].astype(np.float32).reshape(h0, 1, 3), (1, 1), sigmaX=0, sigmaY=max(12.0, h0 * 0.05)).reshape(h0, 3)
            fill[:, W - pr:, :] = rp[ys][:, None, :] * (1.0 - t) + edge_r[ys][:, None, :] * t
        if pt or pb:
            top_col = np.median(img[:max(4, min(12, h0 // 40)), :].reshape(-1, 3), axis=0)
            bot_col = np.median(img[h0 - max(4, min(12, h0 // 40)):, :].reshape(-1, 3), axis=0)
            if pt:
                fill[:pt, pl:pl + w0, :] = top_col
            if pb:
                fill[pt + h0:, pl:pl + w0, :] = bot_col
        # 极轻的四周压暗 + 噪声，保留一点摄影棚背景纸的空间感
        yy = np.linspace(0.0, 1.0, H, dtype=np.float32)[:, None]
        xx = np.linspace(0.0, 1.0, W, dtype=np.float32)[None, :]
        edge = np.maximum(np.maximum(xx, 1.0 - xx), np.maximum(yy, 1.0 - yy))
        fill *= (1.0 - 0.035 * np.clip((edge - 0.50) / 0.50, 0, 1))[:, :, None]
        noise = np.zeros(fill.shape, np.float32)
        cv2.randn(noise, 0.0, 0.5)
        fill = np.clip(fill + noise, 0, 255)
    else:
        # 复杂背景：以全图均色打底，再铺镜像条带；条带保留边缘的竖向结构
        #（柜体、墙面、绿植），比 1px 边缘复制更自然，也不会把人物镜像成第二个“鬼影”。
        fill = np.ones((H, W, 3), np.float32) * img.reshape(-1, 3).mean(axis=0)
        if pl:
            left = cv2.flip(img[:, :sw], 1)
            left = cv2.resize(left, (pl, h0), interpolation=cv2.INTER_LINEAR)
            fill[pt:pt + h0, :pl] = left
        if pr:
            right = cv2.flip(img[:, w0 - sw:], 1)
            right = cv2.resize(right, (pr, h0), interpolation=cv2.INTER_LINEAR)
            fill[pt:pt + h0, W - pr:] = right
        if pt:
            top = cv2.flip(img[:sh, :], 0)
            top = cv2.resize(top, (w0, pt), interpolation=cv2.INTER_LINEAR)
            fill[:pt, pl:pl + w0] = top
        if pb:
            bot = cv2.flip(img[h0 - sh:, :], 0)
            bot = cv2.resize(bot, (w0, pb), interpolation=cv2.INTER_LINEAR)
            fill[pt + h0:, pl:pl + w0] = bot

        # 强景深虚化，只留色块与光影方向；sigma 随短边自适应，避免小图糊成一片。
        sigma = max(16.0, min(w0, h0) * 0.040)
        fill = cv2.GaussianBlur(fill, (0, 0), sigmaX=sigma, sigmaY=sigma)
        yy = np.linspace(0.0, 1.0, H, dtype=np.float32)[:, None]
        xx = np.linspace(0.0, 1.0, W, dtype=np.float32)[None, :]
        edge = np.maximum(np.maximum(xx, 1.0 - xx), np.maximum(yy, 1.0 - yy))
        fill *= (1.0 - 0.08 * np.clip((edge - 0.50) / 0.50, 0, 1))[:, :, None]
        noise = np.zeros(fill.shape, np.float32)
        cv2.randn(noise, 0.0, 0.6)
        fill = np.clip(fill + noise, 0, 255)

    # 原图矩形 alpha=1（逐像素保留），只向外羽化一圈，避免“照片贴在模糊底上的硬边”。
    canvas = cv2.copyMakeBorder(img, pt, pb, pl, pr, cv2.BORDER_REPLICATE).astype(np.float32)
    m = np.zeros((H, W), np.float32)
    m[pt:pt + h0, pl:pl + w0] = 1.0
    seam = max(28.0, min(w0, h0) * 0.070)
    m = cv2.GaussianBlur(m, (0, 0), sigmaX=seam, sigmaY=seam)
    m[pt:pt + h0, pl:pl + w0] = 1.0
    out = fill * (1.0 - m[:, :, None]) + canvas * m[:, :, None]
    out = np.clip(out, 0, 255).astype(np.uint8)
    if not _write(dst, out):
        return _out(False, error="无法写入结果")
    return _out(True, output=os.path.abspath(dst), width=int(W), height=int(H),
                op="b3_extend", info="只扩背景，原图逐像素保留", engine="OpenCV 背景延展 + 景深虚化")



OPS = {
    "a1_bg": a1_bg,
    "a2_retouch": a2_retouch,
    "a3_privacy": a3_privacy,
    "a4_crop": a4_crop,
    "a5_score": a5_score,
    "a8_clarity": a8_clarity,
    "a6_search": a6_search,
    "b1_matte": b1_matte,
    "b2_mask_quick": b2_mask_quick,
    "b3_extend": b3_extend,
    "b4_restore": b4_restore,
    "b6_relight": b6_relight,
}


def main():
    if len(sys.argv) < 4:
        print("usage: ai_tools.py run <input.json> <output.json>")
        return 2
    cmd, in_path, out_path = sys.argv[1].lower(), sys.argv[2], sys.argv[3]
    if cmd != "run":
        print("unknown command")
        return 2
    t0 = time.time()
    try:
        with open(in_path, "r", encoding="utf-8") as f:
            cfg = json.load(f)
        op = str(cfg.get("op") or "").lower()
        fn = OPS.get(op)
        if not fn:
            res = _out(False, error="未知 AI 工具：" + op)
        else:
            res = fn(cfg)
            res.setdefault("elapsed", round(time.time() - t0, 2))
        with open(out_path, "w", encoding="utf-8") as f:
            json.dump(res, f, ensure_ascii=False, indent=2)
        print(json.dumps({"ok": res.get("ok"), "op": op, "elapsed": res.get("elapsed")}, ensure_ascii=False))
        return 0 if res.get("ok") else 1
    except Exception as ex:
        res = _out(False, error="AI 工具异常：" + str(ex))
        try:
            with open(out_path, "w", encoding="utf-8") as f:
                json.dump(res, f, ensure_ascii=False, indent=2)
        except Exception:
            pass
        print(json.dumps(res, ensure_ascii=False))
        return 1


if __name__ == "__main__":
    sys.exit(main())
