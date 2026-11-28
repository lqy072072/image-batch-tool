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
    mode = (cfg.get("mode") or "edit").strip().lower()
    if mode not in ("edit", "portrait"):
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

        p = model_paths(cfg)
        exe = runtime_exe(cfg)
        prompt_used = _prompt_for(mode, prompt)
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
            "-r", src_png,
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
        out = _blend(img_work, gen, strength)
        face_count = 0
        face_state = "off"
        if mode == "portrait" and cfg.get("protect_face", True):
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
