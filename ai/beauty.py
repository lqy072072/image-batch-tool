#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
GFPGAN v1.4 人脸美颜（纯AI）
用法: python beauty.py <输入图片> <输出图片> [strength 0-1]
"""
import sys, os, json
import numpy as np
import cv2
from imgio import imread_u, imwrite_u
import onnxruntime as ort

BASE = os.path.dirname(os.path.abspath(__file__))
MODEL_DIR = os.path.join(BASE, "models")
GFPGAN_PATH = os.path.join(MODEL_DIR, "GFPGANv1.4.onnx")
YUNET_PATH = os.path.join(MODEL_DIR, "face_detection_yunet.onnx")

_sessions = {}

def get_session(path):
    if path not in _sessions:
        opts = ort.SessionOptions()
        opts.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        _sessions[path] = ort.InferenceSession(path, sess_options=opts,
            providers=["CPUExecutionProvider"])
    return _sessions[path]

def detect_faces(img):
    """返回 [(cx, cy, face_w), ...] 人脸中心(原图坐标)和预估人脸宽度"""
    h, w = img.shape[:2]
    det = get_session(YUNET_PATH)
    input_size = 640
    scale = min(input_size / w, input_size / h)
    new_w, new_h = int(w * scale), int(h * scale)
    resized = cv2.resize(img, (new_w, new_h))
    pad_img = np.zeros((input_size, input_size, 3), dtype=np.uint8)
    pad_img[:new_h, :new_w] = resized
    inp = pad_img.astype(np.float32)
    inp = np.transpose(inp, (2, 0, 1))[None, ...]
    outputs = det.run(None, {det.get_inputs()[0].name: inp})
    out_map = {o.name: outputs[i][0] for i, o in enumerate(det.get_outputs())}

    all_dets = []
    for stride in [8, 16, 32]:
        grid = input_size // stride
        cls = out_map[f"cls_{stride}"]
        obj = out_map[f"obj_{stride}"]
        bbox = out_map[f"bbox_{stride}"]
        scores = cls[:, 0] * obj[:, 0]
        for gy in range(grid):
            for gx in range(grid):
                idx = gy * grid + gx
                if scores[idx] < 0.6:
                    continue
                cx = (gx + 0.5 + bbox[idx, 0]) * stride
                cy = (gy + 0.5 + bbox[idx, 1]) * stride
                # 预测的bbox宽高 × stride × 3.5 = 实际人脸大小（640空间）
                pred_w = bbox[idx, 2] * stride * 3.5
                # 转回原图坐标
                cx_o = cx / scale
                cy_o = cy / scale
                fw_o = max(pred_w / scale, 40)  # 最小40px
                all_dets.append((cx_o, cy_o, fw_o, float(scores[idx])))

    if not all_dets:
        return []

    # NMS：按中心距离合并
    all_dets.sort(key=lambda d: -d[3])
    keep = []
    for det in all_dets:
        cx, cy, fw, sc = det
        merged = False
        for (kcx, kcy, kfw, ksc) in keep:
            dist = ((cx - kcx)**2 + (cy - kcy)**2) ** 0.5
            if dist < (fw + kfw) * 0.35:
                merged = True
                break
        if not merged:
            keep.append(det)
    return keep

def process_image(input_path, output_path, strength=0.7):
    img = imread_u(input_path)
    if img is None:
        return {"ok": False, "error": "cannot read: " + input_path}
    h, w = img.shape[:2]
    faces = detect_faces(img)
    if len(faces) == 0:
        if output_path.lower().endswith(('.png', '.tif', '.tiff', '.bmp')):
            imwrite_u(output_path, img)
        else:
            imwrite_u(output_path, img, [cv2.IMWRITE_JPEG_QUALITY, 95])
        return {"ok": True, "faces": 0}

    session = get_session(GFPGAN_PATH)
    in_name = session.get_inputs()[0].name
    out_name = session.get_outputs()[0].name

    result = img.copy()
    n = 0
    for (cx, cy, fw, sc) in faces:
        # 正方形裁剪，边长 = 人脸宽度的1.7倍
        side = fw * 1.7
        crop_cy = cy - fw * 0.08
        x1 = int(cx - side / 2)
        y1 = int(crop_cy - side / 2)
        x2 = int(cx + side / 2)
        y2 = int(crop_cy + side / 2)
        x1 = max(0, x1); y1 = max(0, y1)
        x2 = min(w, x2); y2 = min(h, y2)
        cw = x2 - x1; ch = y2 - y1
        if cw < 20 or ch < 20:
            continue

        crop = img[y1:y2, x1:x2]
        crop_512 = cv2.resize(crop, (512, 512), interpolation=cv2.INTER_LINEAR)

        inp = crop_512.astype(np.float32) / 255.0
        inp = (inp - 0.5) / 0.5
        inp = cv2.cvtColor(inp, cv2.COLOR_BGR2RGB)
        inp = np.transpose(inp, (2, 0, 1))[None, ...]
        out = session.run([out_name], {in_name: inp})[0]
        o = out[0]
        o = np.transpose(o, (1, 2, 0))
        o = (o * 0.5 + 0.5) * 255.0
        o = np.clip(o, 0, 255).astype(np.uint8)
        o = cv2.cvtColor(o, cv2.COLOR_RGB2BGR)
        o = cv2.resize(o, (cw, ch), interpolation=cv2.INTER_LINEAR)

        # 细节保留式合成：只用 AI 输出的低频（修斑点/肤色/噪点），
        # 原图高频纹理按增益回注，避免 512 输入再放大把细节糊掉
        src = crop.astype(np.float32)
        sigma = max(1.0, min(crop.shape[:2]) * 0.004)
        src_low = cv2.GaussianBlur(src, (0, 0), sigma)
        src_hi = src - src_low
        ai_low = cv2.GaussianBlur(o.astype(np.float32), (0, 0), sigma)
        gain = 1.0 + 0.45 * strength * np.clip(np.abs(src_hi).mean(2, keepdims=True) / 8.0, 0, 1)
        det = np.clip(ai_low + src_hi * gain, 0, 255)

        # 羽化贴回（原来是硬矩形，边界会露出方块）
        paste = np.zeros((ch, cw), dtype=np.float32)
        k = max(4, int(min(ch, cw) * 0.06))
        paste[k:ch - k, k:cw - k] = 1.0
        paste = cv2.GaussianBlur(paste, (k * 2 + 1, k * 2 + 1), 0)[:, :, None]

        region = result[y1:y2, x1:x2].astype(np.float32)
        blended = region + (det - region) * strength
        result[y1:y2, x1:x2] = np.clip(region * (1.0 - paste) + blended * paste,
                                       0, 255).astype(np.uint8)
        n += 1

    if output_path.lower().endswith(('.png', '.tif', '.tiff', '.bmp')):
        imwrite_u(output_path, result)          # 无损中转
    else:
        imwrite_u(output_path, result, [cv2.IMWRITE_JPEG_QUALITY, 95])
    return {"ok": True, "faces": n}

if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(json.dumps({"ok": False, "error": "usage"}))
        sys.exit(1)
    inp = sys.argv[1]; outp = sys.argv[2]
    strength = float(sys.argv[3]) if len(sys.argv) > 3 else 0.7
    try:
        r = process_image(inp, outp, strength)
    except Exception as e:
        import traceback
        r = {"ok": False, "error": str(e), "tb": traceback.format_exc()[-800:]}
    print(json.dumps(r))
