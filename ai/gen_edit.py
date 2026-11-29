#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""本地生成式修图引擎（stable-diffusion.cpp + FLUX.2 Klein 4B）。

用法：
  python gen_edit.py run <input.json> <output.json>
  python gen_edit.py status <input.json> <output.json>

设计原则：
- 不把扩散模型塞进实时预览链路，只在用户点「生成」时运行一次。
- 输入图由 C# 先按当前裁切/水平校正保存为 PNG，RAW 不进这里。
- 模型和运行时都可缺失；缺失时返回结构化错误，由 UI 引导下载。
- 输出始终写到独立文件，绝不覆盖原图；C# 再决定加入列表或另存。
"""
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import uuid

import cv2
import numpy as np

BASE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_RUNTIME_DIR = os.path.join(BASE, "genedit")
DEFAULT_MODEL_DIR = os.path.join(BASE, "models", "genedit")

MODEL_FILES = {
    "diffusion": "flux-2-klein-4b-Q4_0.gguf",
    "text_encoder": "Qwen3-4B-Q4_K_M.gguf",
    "vae": "flux2-vae.safetensors",
}
MODEL_MIN_BYTES = {
    "diffusion": 2200 * 1024 * 1024,
    "text_encoder": 2200 * 1024 * 1024,
    "vae": 280 * 1024 * 1024,
}

NEGATIVE_DEFAULT = (
    "blurry, low quality, jpeg artifacts, deformed face, bad anatomy, "
    "extra fingers, missing fingers, extra limbs, oversaturated, plastic skin, "
    "waxy skin, cartoon, anime, painting, watermark, text, logo, frame"
)
PORTRAIT_NEGATIVE = NEGATIVE_DEFAULT + ", different person, identity change, face swap, distorted eyes, crooked teeth"


def _json_read(path):
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def _json_write(path, obj):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(obj, f, ensure_ascii=False, indent=2)


def _env_path(name, default):
    v = os.environ.get(name, "")
    if v:
        return os.path.abspath(os.path.expanduser(v))
    return default


def runtime_dir(cfg=None):
    if cfg and cfg.get("runtime_dir"):
        return os.path.abspath(cfg["runtime_dir"])
    return _env_path("IBT_GENEDIT_RUNTIME_DIR", DEFAULT_RUNTIME_DIR)


def model_dir(cfg=None):
    if cfg and cfg.get("model_dir"):
        return os.path.abspath(cfg["model_dir"])
    return _env_path("IBT_GENEDIT_MODEL_DIR", DEFAULT_MODEL_DIR)


def model_paths(cfg=None):
    d = model_dir(cfg)
    return {k: os.path.join(d, v) for k, v in MODEL_FILES.items()}


def runtime_exe(cfg=None):
    return os.path.join(runtime_dir(cfg), "sd-cli.exe")


def _check_model(cfg=None):
    paths = model_paths(cfg)
    missing = []
    sizes = {}
    for k, p in paths.items():
        if not os.path.isfile(p):
            missing.append({"kind": k, "path": p, "reason": "missing"})
            continue
        try:
            size = os.path.getsize(p)
        except Exception:
            size = 0
        sizes[k] = size
        if size < MODEL_MIN_BYTES[k]:
            missing.append({"kind": k, "path": p, "reason": "too_small", "size": size,
                            "min_size": MODEL_MIN_BYTES[k]})
    exe = runtime_exe(cfg)
    runtime_ok = os.path.isfile(exe)
    if not runtime_ok:
        missing.append({"kind": "runtime", "path": exe, "reason": "missing"})
    return {
        "ok": runtime_ok and not missing,
        "runtime": exe,
        "runtime_ok": runtime_ok,
        "model_dir": model_dir(cfg),
        "models": paths,
        "sizes": sizes,
        "missing": missing,
    }


def _status(cfg=None):
    st = _check_model(cfg)
    st["sd_cli_help_ok"] = None
    if st.get("runtime_ok"):
        try:
            p = subprocess.run([st["runtime"], "-h"], capture_output=True,
                               text=True, encoding="utf-8", errors="replace", timeout=8,
                               creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            out = (p.stdout or "") + (p.stderr or "")
            st["sd_cli_help_ok"] = ("--diffusion-model" in out and "--llm" in out)
        except Exception as ex:
            st["runtime_error"] = str(ex)
    return st


def _fit_size(w, h, max_side):
    """按长边限制缩放，并保证宽高为 64 的倍数（避免 VAE 解码出现黑边）。"""
    if max_side and max(w, h) > max_side:
        s = float(max_side) / float(max(w, h))
        w = int(round(w * s))
        h = int(round(h * s))
    w = max(256, int(round(w / 64.0) * 64))
    h = max(256, int(round(h / 64.0) * 64))
    return w, h


def _prompt_for(mode, prompt):
    prompt = (prompt or "").strip()
    if not prompt:
        return ""
    if mode == "portrait":
        return (
            "Edit this portrait photograph. Keep the same person's identity, facial structure, "
            "pose, expression and background unless the instruction explicitly asks to change them. "
            "Apply the following retouch request: " + prompt + ". "
            "Natural skin texture, realistic pores, soft studio lighting, coherent shadows, "
            "sharp eyes, no plastic skin, no waxy skin, no cartoon style, no text or watermark."
        )
    return (
        "Edit this photograph according to the instruction: " + prompt + ". "
        "Preserve the composition, framing and all objects that are not explicitly mentioned. "
        "Keep lighting, perspective and colors coherent; photorealistic result; "
        "no text, no watermark, no logo, no frame."
    )


def _load_mask(path, width, height, soft=0):
    """读取涂抹/扩图遮罩，统一到目标尺寸；白色=需要重绘。"""
    if not path or not os.path.isfile(path):
        return None
    raw = cv2.imdecode(np.fromfile(path, dtype=np.uint8), cv2.IMREAD_GRAYSCALE)
    if raw is None:
        return None
    if raw.shape[:2] != (height, width):
        raw = cv2.resize(raw, (width, height), interpolation=cv2.INTER_NEAREST)
    mask = (raw > 32).astype(np.float32)
    if soft and soft > 0:
        mask = cv2.GaussianBlur(mask, (0, 0), sigmaX=max(0.5, float(soft)))
    return np.clip(mask, 0, 1)


def _border_color_match(gen, orig_crop, rect, pad):
    """把模型续画区域的平均颜色向原图边缘拉齐，压掉米黄/灰底跳色。"""
    h, w = gen.shape[:2]
    pl, pt, pw, ph = rect
    inner = np.zeros((h, w), np.uint8)
    bx0, by0 = max(0, pl + 6), max(0, pt + 6)
    bx1, by1 = min(w, pl + pw - 6), min(h, pt + ph - 6)
    if bx1 > bx0 and by1 > by0:
        inner[by0:by1, bx0:bx1] = 255
        inner_band = cv2.bitwise_and(inner, cv2.bitwise_not(cv2.erode(inner, np.ones((12, 12), np.uint8))))
    else:
        inner_band = inner
    outer = np.zeros((h, w), np.uint8)
    ox0, oy0 = max(0, pl - pad), max(0, pt - pad)
    ox1, oy1 = min(w, pl + pw + pad), min(h, pt + ph + pad)
    outer[oy0:oy1, ox0:ox1] = 255
    outer = cv2.bitwise_not(outer)
    orig_mean = cv2.mean(orig_crop, mask=inner_band[pt:pt + ph, pl:pl + pw] if inner_band.shape[:2] == (h, w) else None)[:3]
    gen_mean = cv2.mean(gen, mask=outer)[:3]
    if min(gen_mean) < 4 or min(orig_mean) < 4:
        return gen
    gain = np.array([max(0.72, min(1.38, orig_mean[i] / gen_mean[i])) for i in range(3)], np.float32)
    corrected = gen.astype(np.float32) * gain.reshape(1, 1, 3)
    return np.clip(corrected, 0, 255).astype(np.uint8)


def _trim_dark_border(img):
    """裁掉截图/导出图常见的 1–8px 纯黑边，避免扩图时把黑边放大成黑条。"""
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


def _flat_top(img):
    """判断顶部是否为干净的纯色背景（人像棚拍、产品图、天空）。"""
    h, w = img.shape[:2]
    band = img[:max(6, h // 18), :]
    gray = cv2.cvtColor(band, cv2.COLOR_BGR2GRAY)
    return float(gray.std()) < 22.0 and float(gray.mean()) > 55.0


def _local_extend(canvas, rect, seed=0):
    """纯色/渐变边缘的扩图：只延展边界颜色，不生成新物体，边界羽化后几乎无接缝。"""
    pl, pt, pw, ph = rect
    h, w = canvas.shape[:2]
    blur = cv2.GaussianBlur(canvas, (0, 0), sigmaX=max(0.8, min(w, h) * 0.0018))
    noise = np.zeros(canvas.shape, np.float32)
    cv2.randn(noise, 0.0, 1.8)
    ext = np.clip(blur.astype(np.float32) + noise, 0, 255)
    m = np.ones((h, w), np.float32)
    m[pt:pt + ph, pl:pl + pw] = 0.0
    m = cv2.GaussianBlur(m, (0, 0), sigmaX=max(2.0, min(w, h) * 0.004))
    m[pt + 2:pt + ph - 2, pl + 2:pl + pw - 2] = 0.0
    m = m[:, :, None]
    return np.clip(canvas.astype(np.float32) * (1.0 - m) + ext * m, 0, 255).astype(np.uint8)


def _feather_rect(width, height, rect, feather):
    """生成一个“中间为 0、外侧为 1”的柔和回贴遮罩，用于保护原图矩形。"""
    pl, pt, pw, ph = rect
    m = np.ones((height, width), np.float32)
    m[pt:pt + ph, pl:pl + pw] = 0.0
    f = max(1.0, float(feather))
    m = cv2.GaussianBlur(m, (0, 0), sigmaX=f)
    # 中心仍严格为 0，确保原图区域逐像素不被模型改动。
    m[pt + int(f):pt + ph - int(f), pl + int(f):pl + pw - int(f)] = 0.0
    return np.clip(m, 0, 1)


def _blend(orig, gen, strength):
    """按强度回混原图，强度越低越保守。"""
    if strength is None:
        strength = 0.78
    strength = max(0.05, min(1.0, float(strength)))
    if gen.shape[:2] != orig.shape[:2]:
        gen = cv2.resize(gen, (orig.shape[1], orig.shape[0]), interpolation=cv2.INTER_LANCZOS4)
    out = orig.astype(np.float32) * (1.0 - strength) + gen.astype(np.float32) * strength
    return np.clip(out, 0, 255).astype(np.uint8)


def _nms(boxes, iou_thr=0.3):
    """boxes: [x, y, w, h, score]，按分数做简单 NMS。"""
    if not boxes:
        return []
    boxes = sorted(boxes, key=lambda b: b[4], reverse=True)
    keep = []
    while boxes:
        a = boxes.pop(0)
        keep.append(a)
        rest = []
        for b in boxes:
            ax1, ay1, ax2, ay2 = a[0], a[1], a[0] + a[2], a[1] + a[3]
            bx1, by1, bx2, by2 = b[0], b[1], b[0] + b[2], b[1] + b[3]
            ix = max(0, min(ax2, bx2) - max(ax1, bx1))
            iy = max(0, min(ay2, by2) - max(ay1, by1))
            inter = ix * iy
            union = a[2] * a[3] + b[2] * b[3] - inter
            if union <= 0 or inter / float(union) <= iou_thr:
                rest.append(b)
        boxes = rest
    return keep


def _detect_faces(img):
    """用 onnxruntime 跑 YuNet，返回原图坐标系的人脸框 [(x,y,w,h)]。

    OpenCV 5.0 的 DNN 解析器读不了这份 2023mar YuNet，因此绕过 cv2.dnn，
    直接按 640x640 输入解码 cls/obj/bbox 三个尺度。
    """
    import onnxruntime as ort
    model = os.path.join(BASE, "models", "face_detection_yunet.onnx")
    if not os.path.isfile(model):
        return []
    h0, w0 = img.shape[:2]
    inp = cv2.resize(img, (640, 640), interpolation=cv2.INTER_LINEAR)
    blob = inp.astype(np.float32).transpose(2, 0, 1)[None]
    sess = ort.InferenceSession(model, providers=["CPUExecutionProvider"])
    names = [o.name for o in sess.get_outputs()]
    outs = dict(zip(names, sess.run(None, {sess.get_inputs()[0].name: blob})))
    boxes = []
    for stride in (8, 16, 32):
        cls = outs.get("cls_%d" % stride)
        obj = outs.get("obj_%d" % stride)
        bbox = outs.get("bbox_%d" % stride)
        if cls is None or obj is None or bbox is None:
            continue
        c = cls[0, :, 0]
        o = obj[0, :, 0]
        score = np.sqrt(np.clip(c, 0, 1) * np.clip(o, 0, 1))
        cols = 640 // stride
        for i in np.where(score >= 0.55)[0]:
            row, col = divmod(int(i), cols)
            bb = bbox[0, i]
            cx = (col + float(bb[0])) * stride
            cy = (row + float(bb[1])) * stride
            bw = float(np.exp(min(8.0, float(bb[2]))) * stride)
            bh = float(np.exp(min(8.0, float(bb[3]))) * stride)
            x1 = cx - bw / 2.0
            y1 = cy - bh / 2.0
            # 640x640 是直接拉伸，按原图比例映射回来
            sx, sy = w0 / 640.0, h0 / 640.0
            boxes.append([x1 * sx, y1 * sy, bw * sx, bh * sy, float(score[i])])
    boxes = _nms(boxes)
    return [(int(round(b[0])), int(round(b[1])), int(round(b[2])), int(round(b[3]))) for b in boxes]


def _protect_faces(orig, gen, model_dir_path):
    """保脸：只把脸内 18% 的原图纹理回注，保留妆容/肤色变化，同时稳住身份与五官。"""
    try:
        faces = _detect_faces(orig)
        if not faces:
            return gen, 0, "no_face"
        h, w = orig.shape[:2]
        mask = np.zeros((h, w), np.float32)
        for x, y, bw, bh in faces:
            cx, cy = x + bw / 2.0, y + bh * 0.54
            ax, ay = max(8, bw * 0.53), max(10, bh * 0.68)
            cv2.ellipse(mask, (int(cx), int(cy)), (int(ax), int(ay)), 0, 0, 360, 1.0, -1)
        mask = cv2.GaussianBlur(mask, (0, 0), sigmaX=max(4, int(min(w, h) * 0.012)))
        mask = np.clip(mask * 0.18, 0, 0.18)[:, :, None]
        out = gen.astype(np.float32) * (1.0 - mask) + orig.astype(np.float32) * mask
        return np.clip(out, 0, 255).astype(np.uint8), int(len(faces)), "ok"
    except Exception as ex:
        return gen, 0, "face_error:" + str(ex)[:80]


def _run(cfg):
    t0 = time.time()
    prompt = (cfg.get("prompt") or "").strip()
    if not prompt:
        return {"ok": False, "code": "empty_prompt", "error": "提示词为空，请先写一句想怎么修。"}
    src = (cfg.get("input") or "").strip()
    dst = (cfg.get("output") or "").strip()
    if not src or not os.path.isfile(src):
        return {"ok": False, "code": "input_missing", "error": "输入图片不存在：" + src}
    if not dst:
        return {"ok": False, "code": "output_missing", "error": "没有指定输出文件。"}

    st = _check_model(cfg)
    if not st["ok"]:
        return {
            "ok": False,
            "code": "model_missing",
            "error": "生成式修图模型尚未安装。",
            "missing": st["missing"],
            "runtime": st["runtime"],
            "model_dir": st["model_dir"],
        }

    import cv2
    import numpy as np
    from imgio import imread_u, imwrite_u

    img = imread_u(src)
    if img is None:
        return {"ok": False, "code": "decode_failed", "error": "无法读取输入图片（RAW 请先在编辑器里预览/裁切后再生成）。"}
    img = _trim_dark_border(img)
    mode = (cfg.get("mode") or "edit").strip().lower()
    if mode not in ("edit", "portrait"):
        mode = "edit"
    task = (cfg.get("task") or "edit").strip().lower()
    if task not in ("edit", "outpaint", "colorize", "inpaint"):
        task = "edit"
    orig_rect = None
    mask_cfg = (cfg.get("mask") or "").strip()
    base_img = img.copy()
    # 生成式扩图：先把画布按比例向外扩，用镜像边缘做初始填充，再交给模型重绘。
    # 只扩大画布，不裁剪原图；原图区域始终保留。
    if task == "outpaint":
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
        elif direction == "vertical":
            pl, pr, pt, pb = 0, 0, py, py
        elif direction == "left":
            pl, pr, pt, pb = px, 0, 0, 0
        elif direction == "right":
            pl, pr, pt, pb = 0, px, 0, 0
        elif direction == "top":
            pl, pr, pt, pb = 0, 0, py, 0
        elif direction == "bottom":
            pl, pr, pt, pb = 0, 0, 0, py
        else:
            pl, pr, pt, pb = px, px, py, py
        canvas = cv2.copyMakeBorder(img, pt, pb, pl, pr, cv2.BORDER_REPLICATE)
        # 人像竖图不向下拉长身体：四周/上下模式自动跳过底部，避免模型凭空生成衣服或腿。
        if pb and direction in ("all", "vertical") and h0 / max(1.0, float(w0)) > 1.08:
            canvas = canvas[:pt + h0, :]
            pb = 0
        # 只保留原图，不复制脸部；新增区域交给遮罩重绘。
        img = canvas
        orig_rect = (pl, pt, w0, h0)
        if pt and _flat_top(base_img):
            out = _local_extend(img, orig_rect, cfg.get("seed", 0) or 0)
            if not imwrite_u(dst, out):
                return {"ok": False, "code": "output_write_failed", "error": "无法写入扩图结果。"}
            return {
                "ok": True,
                "output": dst,
                "width": int(out.shape[1]),
                "height": int(out.shape[0]),
                "elapsed": round(time.time() - t0, 2),
                "mode": "edit",
                "task": "outpaint",
                "model": "local-edge-extend",
                "prompt": prompt,
                "negative": "",
                "steps": 0,
                "strength": 0,
                "seed": cfg.get("seed", 0),
                "backend": "local",
                "face_count": 0,
                "protect_face": "off",
                "vram_gb": 0,
                "runtime": "",
                "log_tail": "flat-background local extension",
            }
    elif task == "colorize":
        # 只做上色时保持构图与颗粒，不做人像保脸回注。
        mode = "edit"
    strength = cfg.get("strength", 0.78)
    try:
        strength = float(strength)
    except Exception:
        strength = 0.78
    steps = cfg.get("steps", 4)
    try:
        steps = max(2, min(20, int(steps)))
    except Exception:
        steps = 4
    seed = cfg.get("seed", -1)
    try:
        seed = int(seed)
    except Exception:
        seed = -1
    if seed < 0:
        seed = int(time.time() * 1000) & 0x7fffffff
    max_side = cfg.get("max_side", 0)
    try:
        max_side = int(max_side)
    except Exception:
        max_side = 0
    if max_side <= 0:
        max_side = 1024
    max_side = max(256, min(1536, max_side))
    h, w = img.shape[:2]
    tw, th = _fit_size(w, h, max_side)

    work = tempfile.mkdtemp(prefix="ibt_genedit_")
    src_png = os.path.join(work, "input.png")
    raw_out = os.path.join(work, "raw.png")
    try:
        # 输入图先缩到工作尺寸，避免 6000 万像素直接进扩散模型。
        if (tw, th) != (w, h):
            img_work = cv2.resize(img, (tw, th), interpolation=cv2.INTER_LANCZOS4)
        else:
            img_work = img
        if not imwrite_u(src_png, img_work):
            return {"ok": False, "code": "input_write_failed", "error": "无法写入临时输入图。"}

        mask_png = os.path.join(work, "mask.png")
        mask_full = None
        if task == "outpaint" and orig_rect:
            pl, pt, pw, ph = orig_rect
            mask_full = np.ones((h, w), np.uint8) * 255
            mask_full[pt:pt + ph, pl:pl + pw] = 0
            mask_full = cv2.GaussianBlur(mask_full, (0, 0), sigmaX=max(4.0, min(w, h) * 0.012))
        elif task == "inpaint":
            mask_full = _load_mask(mask_cfg, w, h, soft=max(2.0, min(w, h) * 0.003))
            if mask_full is None:
                return {"ok": False, "code": "mask_missing", "error": "没有收到涂抹遮罩，请重新涂抹要消除的区域。"}
            mask_full = (mask_full * 255).astype(np.uint8)
        if mask_full is not None:
            if mask_full.shape[:2] != (th, tw):
                mask_full = cv2.resize(mask_full, (tw, th), interpolation=cv2.INTER_LINEAR)
            if not imwrite_u(mask_png, mask_full):
                return {"ok": False, "code": "mask_write_failed", "error": "无法写入临时遮罩。"}

        p = model_paths(cfg)
        exe = runtime_exe(cfg)
        prompt_used = _prompt_for(mode, prompt)
        if task == "outpaint":
            prompt_used += (" Outpaint and extend the photograph only inside the white mask area. "
                            "Continue the existing perspective, lighting, textures, wall and background "
                            "structure. Do not duplicate faces, limbs, people, text or objects; "
                            "do not shrink, move or restyle the original subject.")
        elif task == "inpaint":
            prompt_used += (" Remove the unwanted objects inside the white mask area and reconstruct "
                            "the surrounding background with matching texture, perspective, lighting "
                            "and color. Leave everything outside the mask unchanged; do not add new "
                            "objects, text, people or extra limbs.")
        elif task == "colorize":
            prompt_used += (" Colorize this black-and-white photograph with natural, historically plausible "
                            "colors. Preserve all facial features, grain, scratches and composition; "
                            "do not beautify, reshape or add objects.")
        negative = cfg.get("negative") or (PORTRAIT_NEGATIVE if mode == "portrait" else NEGATIVE_DEFAULT)
        backend = (cfg.get("backend") or "auto").strip()
        vram = cfg.get("vram_gb")
        try:
            vram = float(vram)
        except Exception:
            vram = 0.0

        args = [
            exe,
            "--diffusion-model", p["diffusion"],
            "--vae", p["vae"],
            "--llm", p["text_encoder"],
            "-p", prompt_used,
            "-n", negative,
            "-o", raw_out,
            "--cfg-scale", "1.0",
            "--guidance", "1.0",
            "--steps", str(steps),
            "--sampling-method", "euler",
            "--seed", str(seed),
            "-W", str(tw),
            "-H", str(th),
            "--offload-to-cpu",
            "--vae-conv-direct",
        ]
        if mask_full is not None:
            args += ["-i", src_png, "--mask", mask_png, "--strength", str(max(0.35, min(1.0, strength)))]
        else:
            args += ["-r", src_png]
        if backend and backend.lower() not in ("auto", "default"):
            args += ["--backend", backend]
        # 6GB 卡留出桌面/VAE 余量，避免驱动把整机拖死；8GB 以上给 7GB 左右。
        if vram > 0:
            if vram <= 6.5:
                args += ["--max-vram", "5.2"]
            elif vram <= 8.5:
                args += ["--max-vram", "7.2"]
            elif vram <= 12.5:
                args += ["--max-vram", "11.0"]

        env = os.environ.copy()
        env["PYTHONIOENCODING"] = "utf-8"
        env["PYTHONUTF8"] = "1"
        # 让 sd-cli 的日志走 UTF-8，避免中文路径在日志里乱码。
        env["GGML_LOG_LEVEL"] = env.get("GGML_LOG_LEVEL", "info")
        timeout_sec = cfg.get("timeout_sec", 900)
        try:
            timeout_sec = max(60, min(2400, int(timeout_sec)))
        except Exception:
            timeout_sec = 900

        proc = subprocess.run(
            args,
            cwd=runtime_dir(cfg),
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=timeout_sec,
            env=env,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        log = ((proc.stdout or "") + "\n" + (proc.stderr or "")).strip()
        if proc.returncode != 0 or not os.path.isfile(raw_out) or os.path.getsize(raw_out) < 1000:
            tail = re.sub(r"\s+", " ", log)[-700:]
            return {
                "ok": False,
                "code": "generation_failed",
                "error": "模型运行失败（退出码 %s）。" % proc.returncode,
                "detail": tail,
                "cmd": " ".join(args),
            }

        gen = imread_u(raw_out)
        if gen is None:
            return {"ok": False, "code": "decode_output_failed", "error": "模型输出无法解码。"}
        if mask_full is not None:
            m = mask_full.astype(np.float32) / 255.0
            if m.shape[:2] != gen.shape[:2]:
                m = cv2.resize(m, (gen.shape[1], gen.shape[0]), interpolation=cv2.INTER_LINEAR)
            if m.ndim == 2:
                m = m[:, :, None]
            base = img_work
            if base.shape[:2] != gen.shape[:2]:
                base = cv2.resize(base, (gen.shape[1], gen.shape[0]), interpolation=cv2.INTER_LANCZOS4)
            out = (base.astype(np.float32) * (1.0 - m) + gen.astype(np.float32) * m).clip(0, 255).astype(np.uint8)
        else:
            out = _blend(img_work, gen, strength)
        if task == "outpaint":
            # 工作尺寸只用于扩散；输出恢复到扩展后的画布尺寸，避免扩图后反而比原图小。
            out = cv2.resize(out, (img.shape[1], img.shape[0]), interpolation=cv2.INTER_LANCZOS4)
            if orig_rect:
                # 原图矩形逐像素贴回，并把模型在遮罩边缘产生的黑边/跳色用原图边缘延展盖住。
                pl, pt, pw, ph = orig_rect
                H, W = img.shape[:2]
                pb2 = max(0, H - pt - ph)
                pad = max(6, int(round(min(pw, ph) * 0.03)))
                crop = img[pt:pt + ph, pl:pl + pw]
                out = _border_color_match(out, crop, orig_rect, pad)
                cover_full = cv2.copyMakeBorder(crop, pt + pad, pb2 + pad, pl + pad,
                                                max(0, W - pl - pw) + pad, cv2.BORDER_REPLICATE)
                cover = cover_full[pad:pad + H, pad:pad + W]
                expanded = (max(0, pl - pad), max(0, pt - pad),
                            min(W, pl + pw + pad) - max(0, pl - pad),
                            min(H, pt + ph + pad) - max(0, pt - pad))
                protect = _feather_rect(W, H, expanded, max(3.0, min(pw, ph) * 0.012))
                protect = protect[:, :, None]
                out = (out.astype(np.float32) * protect + cover.astype(np.float32) * (1.0 - protect)).clip(0, 255).astype(np.uint8)
        elif task == "inpaint":
            if out.shape[:2] != img.shape[:2]:
                out = cv2.resize(out, (img.shape[1], img.shape[0]), interpolation=cv2.INTER_LANCZOS4)
        face_count = 0
        face_state = "off"
        if mode == "portrait" and task not in ("inpaint", "outpaint") and cfg.get("protect_face", True):
            out, face_count, face_state = _protect_faces(img_work, out, model_dir(cfg))

        os.makedirs(os.path.dirname(os.path.abspath(dst)) or ".", exist_ok=True)
        if not imwrite_u(dst, out):
            return {"ok": False, "code": "output_write_failed", "error": "无法写入结果：" + dst}

        return {
            "ok": True,
            "output": os.path.abspath(dst),
            "width": int(out.shape[1]),
            "height": int(out.shape[0]),
            "elapsed": round(time.time() - t0, 2),
            "mode": mode,
            "task": task,
            "model": "FLUX.2 Klein 4B (Q4_0)",
            "prompt": prompt,
            "prompt_used": prompt_used,
            "negative": negative,
            "steps": steps,
            "strength": round(strength, 3),
            "seed": seed,
            "backend": backend if backend else "auto",
            "face_count": face_count,
            "protect_face": face_state,
            "vram_gb": vram,
            "runtime": exe,
            "log_tail": re.sub(r"\s+", " ", log)[-500:],
        }
    except subprocess.TimeoutExpired:
        return {"ok": False, "code": "timeout", "error": "生成超时。6GB 显卡建议先选 768 长边、4 步；或关闭其他占用显存的程序。"}
    except Exception as ex:
        return {"ok": False, "code": "exception", "error": "生成失败：" + str(ex)}
    finally:
        try:
            shutil.rmtree(work, ignore_errors=True)
        except Exception:
            pass


def main():
    if len(sys.argv) < 2:
        print("usage: gen_edit.py run|status <input.json> <output.json>")
        return 2
    cmd = sys.argv[1].lower()
    if cmd == "status":
        cfg = {}
        out = sys.argv[3] if len(sys.argv) > 3 else ""
        if len(sys.argv) > 2 and os.path.isfile(sys.argv[2]):
            try:
                cfg = _json_read(sys.argv[2])
            except Exception:
                cfg = {}
        res = _status(cfg)
        if out:
            _json_write(out, res)
        else:
            print(json.dumps(res, ensure_ascii=False, indent=2))
        return 0 if res.get("ok") else 1
    if cmd != "run" or len(sys.argv) < 4:
        print("usage: gen_edit.py run <input.json> <output.json>")
        return 2
    cfg = _json_read(sys.argv[2])
    res = _run(cfg)
    _json_write(sys.argv[3], res)
    return 0 if res.get("ok") else 1


if __name__ == "__main__":
    sys.exit(main())
