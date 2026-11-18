"""全图 2x 超分：Real-ESRGAN ONNX 分块推理 + 重叠融合
用法: python sr.py <in> <out> <scale 2|4>
"""
import glob
import json
import os
import sys

import cv2
import numpy as np
import onnxruntime as ort

BASE = os.path.dirname(os.path.abspath(__file__))
MODEL_DIR = os.path.join(BASE, "models")
_sess = None
_model_path = None


def find_model():
    cands = []
    for pat in ("*realesrgan*.onnx", "*esrgan*.onnx", "*RealESRGAN*.onnx", "sr_*.onnx", "*x2*.onnx"):
        cands += glob.glob(os.path.join(MODEL_DIR, pat))
    cands = [c for c in cands if os.path.getsize(c) > 1024 * 1024]
    return cands[0] if cands else None


def session():
    global _sess, _model_path
    if _sess is None:
        _model_path = find_model()
        if not _model_path:
            raise RuntimeError("缺少超分模型：请把 Real-ESRGAN 的 onnx 放进 ai/models")
        opts = ort.SessionOptions()
        opts.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        opts.intra_op_num_threads = max(1, (os.cpu_count() or 4) - 1)
        _sess = ort.InferenceSession(_model_path, sess_options=opts, providers=["CPUExecutionProvider"])
    return _sess


def run_tile(sess, tile, in_name, need_size):
    x = tile[:, :, ::-1].astype(np.float32) / 255.0        # BGR->RGB
    x = np.transpose(x, (2, 0, 1))[None]
    if need_size:
        x = x.astype(np.float32)
    out = sess.run(None, {in_name: x})[0][0]
    out = np.transpose(out, (1, 2, 0))[:, :, ::-1]
    return np.clip(out, 0, 1)


def fallback_upscale(img, scale):
    """未放模型时的兜底：Lanczos 放大 + 多尺度细节回注（不是 AI 超分，但比双线性干净）"""
    h, w = img.shape[:2]
    big = cv2.resize(img, (w * scale, h * scale), interpolation=cv2.INTER_LANCZOS4)
    base = cv2.resize(big, (w, h), interpolation=cv2.INTER_AREA)
    detail = img.astype(np.float32) - base.astype(np.float32)
    detail = cv2.detailEnhance(np.clip(detail + 128, 0, 255).astype(np.uint8), sigma_s=8, sigma_r=0.12).astype(np.float32) - 128
    up_detail = cv2.resize(detail, (w * scale, h * scale), interpolation=cv2.INTER_CUBIC)
    out = big.astype(np.float32) + up_detail * 1.35
    return np.clip(out, 0, 255).astype(np.uint8)


def main():
    if len(sys.argv) < 4:
        print(json.dumps({"ok": False, "error": "args"}))
        return
    src, dst = sys.argv[1], sys.argv[2]
    scale = int(sys.argv[3]) if len(sys.argv) > 3 else 2

    img = cv2.imread(src, cv2.IMREAD_COLOR)
    if img is None:
        print(json.dumps({"ok": False, "error": "无法读取图片"}))
        return

    if not find_model():
        res = fallback_upscale(img, scale) if scale == 2 else cv2.resize(
            img, None, fx=scale, fy=scale, interpolation=cv2.INTER_LANCZOS4)
        if dst.lower().endswith(".png"):
            cv2.imencode(".png", res)[1].tofile(dst)
        else:
            cv2.imencode(".jpg", res, [int(cv2.IMWRITE_JPEG_QUALITY), 96])[1].tofile(dst)
        print(json.dumps({"ok": True, "model": "fallback-lanczos", "w": res.shape[1], "h": res.shape[0]}))
        return

    sess = session()
    in_name = sess.get_inputs()[0].name
    H, W = img.shape[:2]

    tile = 256        # 分块边长（模型输入）
    overlap = 16      # 重叠像素，用于融合接缝

    # 先跑一块探测模型真实放大倍数（x4 模型也能给「2× 导出」用，避免只取左上角）
    probe = cv2.copyMakeBorder(img[:min(tile, H), :min(tile, W)], 0, max(0, tile - min(tile, H)),
                               0, max(0, tile - min(tile, W)), cv2.BORDER_REPLICATE)
    a, b = probe.shape[:2]
    pout = run_tile(sess, probe, in_name, False)
    real_scale = max(1, int(round(pout.shape[0] / float(a))))

    out = np.zeros((H * real_scale, W * real_scale, 3), dtype=np.float32)
    weight = np.zeros((H * real_scale, W * real_scale, 1), dtype=np.float32)

    # 羽化权重（中间 1，边缘渐变）
    ramp = np.ones(tile * real_scale, dtype=np.float32)
    ramp_len = overlap * real_scale
    if ramp_len > 1:
        f = (np.arange(ramp_len, dtype=np.float32) + 1) / ramp_len
        ramp[:ramp_len] = f
        ramp[-ramp_len:] = f[::-1]
    wgt = np.outer(ramp, ramp)[:, :, None]

    ys = list(range(0, max(1, H - tile + 1), tile - overlap))
    xs = list(range(0, max(1, W - tile + 1), tile - overlap))
    if ys[-1] + tile < H:
        ys.append(max(0, H - tile))
    if xs[-1] + tile < W:
        xs.append(max(0, W - tile))

    for y in ys:
        for x in xs:
            patch = img[y:y + tile, x:x + tile]
            ph, pw = patch.shape[:2]
            if ph < tile or pw < tile:
                pad = cv2.copyMakeBorder(patch, 0, tile - ph, 0, tile - pw, cv2.BORDER_REPLICATE)
            else:
                pad = patch
            up = run_tile(sess, pad, in_name, False)
            oh, ow = int(ph * real_scale), int(pw * real_scale)
            up = up[:oh, :ow]
            w = wgt[:oh, :ow]
            oy, ox = y * real_scale, x * real_scale
            out[oy:oy + oh, ox:ox + ow] += up * w
            weight[oy:oy + oh, ox:ox + ow] += w

    weight[weight == 0] = 1
    out = np.clip(out / weight, 0, 1)

    # 轻微降噪回缩，避免超分后噪点放大
    res = (out * 255).astype(np.uint8)
    res = cv2.fastNlMeansDenoisingColored(res, None, 2, 2, 7, 21)
    res = cv2.detailEnhance(res, sigma_s=6, sigma_r=0.10)

    # 模型倍数 != 目标倍数时再缩放到目标（x4 模型出 2× 也走这里）
    if scale != real_scale:
        res = cv2.resize(res, (W * scale, H * scale), interpolation=cv2.INTER_AREA if scale < real_scale else cv2.INTER_LANCZOS4)

    if dst.lower().endswith(".png"):
        cv2.imencode(".png", res)[1].tofile(dst)
    else:
        cv2.imencode(".jpg", res, [int(cv2.IMWRITE_JPEG_QUALITY), 96])[1].tofile(dst)
    print(json.dumps({"ok": True, "model": os.path.basename(_model_path), "w": res.shape[1], "h": res.shape[0]}))


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        print(json.dumps({"ok": False, "error": str(e)}))
