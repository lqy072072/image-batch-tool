"""景深虚化：Depth Anything V2 Small 出深度 + 人脸解析保护主体
用法: python dof.py <in> <out> <strength 0-100> <focus 0-100>
"""
import json
import os
import sys

import cv2
import numpy as np
import onnxruntime as ort

BASE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, BASE)
MODEL = os.path.join(BASE, "models", "depth_vits.onnx")
_sess = None


def session():
    global _sess
    if _sess is None:
        opts = ort.SessionOptions()
        opts.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        opts.intra_op_num_threads = 2
        _sess = ort.InferenceSession(MODEL, sess_options=opts, providers=["CPUExecutionProvider"])
    return _sess


def infer_depth(img):
    size = 518
    h, w = img.shape[:2]
    rgb = cv2.cvtColor(img, cv2.COLOR_BGR2RGB)
    x = cv2.resize(rgb, (size, size), interpolation=cv2.INTER_LINEAR).astype(np.float32) / 255.0
    mean = np.array([0.485, 0.456, 0.406], dtype=np.float32)
    std = np.array([0.229, 0.224, 0.225], dtype=np.float32)
    x = (x - mean) / std
    x = np.transpose(x, (2, 0, 1))[None]
    s = session()
    out = s.run(None, {s.get_inputs()[0].name: x})[0].reshape(size, size)
    d = cv2.resize(out, (w, h), interpolation=cv2.INTER_CUBIC)
    # 百分位拉伸，提升主体/背景分离度
    lo, hi = np.percentile(d, 2), np.percentile(d, 98)
    d = (d - lo) / max(1e-6, hi - lo)
    return np.clip(d, 0, 1)


def person_mask(img):
    try:
        import beauty_pro as bp
        parser = bp.FaceParser()
        labels = parser.parse(img)
        mask = (labels > 0).astype(np.float32)
        k = max(3, int(min(img.shape[:2]) * 0.02))
        mask = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, np.ones((k, k), np.uint8))
        return mask
    except Exception:
        return None


def main():
    if len(sys.argv) < 5:
        print(json.dumps({"ok": False, "error": "args"}))
        return
    src, dst = sys.argv[1], sys.argv[2]
    strength = float(sys.argv[3])
    focus_pct = float(sys.argv[4])
    if not os.path.exists(MODEL):
        print(json.dumps({"ok": False, "error": "缺少模型 depth_vits.onnx"}))
        return

    img = cv2.imread(src, cv2.IMREAD_COLOR)
    if img is None:
        print(json.dumps({"ok": False, "error": "无法读取图片"}))
        return

    depth = infer_depth(img)
    h, w = img.shape[:2]

    # 主体保护（人像解析）；无人时退化为纯深度
    pmask = person_mask(img)
    if pmask is not None and pmask.mean() > 0.02:
        pmask = cv2.GaussianBlur(pmask, (0, 0), max(1.5, min(h, w) * 0.006))
        subject_depth = float(np.median(depth[pmask > 0.6])) if (pmask > 0.6).any() else 0.5
    else:
        pmask = None
        subject_depth = float(np.percentile(depth, 50))

    # 焦平面：以主体深度为基准，滑块只做小幅前后偏移
    focus = float(np.clip(subject_depth + (focus_pct - 50.0) / 100.0 * 0.25, 0, 1))

    diff = np.abs(depth - focus)
    band = 0.10
    wgt = np.clip((diff - band) / max(1e-3, 0.35), 0.0, 1.0)
    wgt = wgt * wgt * (3.0 - 2.0 * wgt)

    # 主体永远清晰：先膨胀主体轮廓，再压掉人物上的虚化（避免人像边缘出现暗色光晕）
    if pmask is not None:
        k = max(9, int(min(h, w) * 0.025) // 2 * 2 + 1)
        pm_d = cv2.dilate((pmask > 0.45).astype(np.uint8), np.ones((k, k), np.uint8))
        pm_prot = cv2.GaussianBlur(pm_d.astype(np.float32), (0, 0), k * 0.9)
        wgt = wgt * (1.0 - np.clip(pm_prot, 0, 1))

    max_sigma = max(1.0, min(h, w) * 0.010 * (strength / 100.0) * 2.2)
    blur2 = cv2.GaussianBlur(img, (0, 0), max_sigma * 0.7)
    blur3 = cv2.GaussianBlur(img, (0, 0), max_sigma * 1.15)

    w2 = np.clip(wgt * 1.4, 0.0, 1.0)[:, :, None]
    w3 = np.clip((wgt - 0.55) * 2.2, 0.0, 1.0)[:, :, None]
    out = img.astype(np.float32) * (1 - w2) + blur2.astype(np.float32) * w2
    out = out * (1 - w3) + blur3.astype(np.float32) * w3
    out = np.clip(out, 0, 255).astype(np.uint8)

    if dst.lower().endswith(".png"):
        cv2.imencode(".png", out)[1].tofile(dst)
    else:
        cv2.imencode(".jpg", out, [int(cv2.IMWRITE_JPEG_QUALITY), 96])[1].tofile(dst)
    print(json.dumps({"ok": True, "w": out.shape[1], "h": out.shape[0],
                      "subject": pmask is not None, "focus": round(focus, 3)}))


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        print(json.dumps({"ok": False, "error": str(e)}))
