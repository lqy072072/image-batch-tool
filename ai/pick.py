"""自动挑片：清晰度（Laplacian 方差）、曝光（过曝/欠曝）、闭眼（468 关键点 EAR）
用法: python pick.py <图片列表txt> <模糊阈值 0-100> <曝光阈值 0-100>
输出 JSON: {"ok":true,"items":[{"path":..,"sharp":..,"exposure":..,"eyes":..,"score":..,"keep":true,"why":".."}]}
"""
import json
import os
import sys

import cv2
from imgio import imread_u, imwrite_u
import numpy as np

BASE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, BASE)

# 468 关键点索引（标准 MediaPipe 拓扑）
L_EYE = [(33, 133), (160, 144), (158, 153)]
R_EYE = [(362, 263), (385, 380), (387, 373)]


def sharpness(gray):
    lap = cv2.Laplacian(gray, cv2.CV_64F, ksize=3)
    var = float(lap.var())
    # 归一到 0-100（经验区间 5~1200）
    v = np.log10(max(1.0, var)) / np.log10(1200.0) * 100.0
    return float(np.clip(v, 0, 100))


def exposure(gray):
    hist = cv2.calcHist([gray], [0], None, [256], [0, 256]).ravel()
    tot = max(1.0, hist.sum())
    clip_hi = hist[245:].sum() / tot
    clip_lo = hist[:12].sum() / tot
    mean = float(np.clip(gray.mean() / 255.0, 0, 1))
    # 目标平均亮度 0.45~0.6，剪裁越少越好
    center = 1.0 - abs(mean - 0.52) / 0.52
    score = 100.0 * (0.6 * center + 0.4 * (1.0 - min(1.0, (clip_hi + clip_lo) * 8)))
    return float(np.clip(score, 0, 100)), float(clip_hi), float(clip_lo), mean


def eye_open_ratio(img):
    """返回 (最小 EAR, 是否检测到人脸)，无脸返回 (None, False)"""
    try:
        import beauty_pro as bp
        faces = bp.detect_faces(img)
        if not faces:
            return None, False
        faces.sort(key=lambda d: -d[2])
        cx, cy, fw, _ = faces[0]
        box = (cx - fw * 0.75, cy - fw * 0.85, cx + fw * 0.75, cy + fw * 0.85)
        mesh = bp.FaceMesh()
        pts = mesh.predict(img, box)
        res = []
        for idxs in (L_EYE, R_EYE):
            (a, b), (c1, d1), (c2, d2) = idxs
            w = np.linalg.norm(pts[a] - pts[b])
            if w < 1:
                continue
            h = (np.linalg.norm(pts[c1] - pts[d1]) + np.linalg.norm(pts[c2] - pts[d2])) / 2.0
            res.append(float(h / w))
        return (min(res), True) if res else (None, False)
    except Exception:
        return None, False


def main():
    if len(sys.argv) < 4:
        print(json.dumps({"ok": False, "error": "args"}))
        return
    list_path = sys.argv[1]
    blur_th = float(sys.argv[2])
    expo_th = float(sys.argv[3])

    paths = []
    with open(list_path, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line:
                paths.append(line)

    items = []
    for p in paths:
        img = imread_u(p, cv2.IMREAD_COLOR)
        if img is None:
            continue
        work = img
        if max(work.shape[:2]) > 1600:
            s = 1600.0 / max(work.shape[:2])
            work = cv2.resize(work, None, fx=s, fy=s, interpolation=cv2.INTER_AREA)
        gray = cv2.cvtColor(work, cv2.COLOR_BGR2GRAY)

        sharp = sharpness(gray)
        expo, clip_hi, clip_lo, mean = exposure(gray)
        ear, has_face = eye_open_ratio(work)

        why = []
        if sharp < blur_th:
            why.append("模糊")
        if expo < expo_th or clip_hi > 0.08:
            why.append("曝光")
        eyes_closed = False
        if has_face and ear is not None and ear < 0.18:
            eyes_closed = True
            why.append("疑似闭眼")

        score = 0.55 * sharp + 0.45 * expo
        if eyes_closed:
            score -= 35
        items.append({
            "path": p,
            "name": os.path.basename(p),
            "sharp": round(sharp, 1),
            "exposure": round(expo, 1),
            "ear": None if ear is None else round(ear, 3),
            "face": has_face,
            "score": round(max(0.0, score), 1),
            "keep": bool(sharp >= blur_th and expo >= expo_th and not eyes_closed and clip_hi <= 0.08),
            "why": why,
        })

    items.sort(key=lambda x: -x["score"])
    print(json.dumps({"ok": True, "items": items, "best": items[0]["path"] if items else None}, ensure_ascii=True))


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        print(json.dumps({"ok": False, "error": str(e)}))
