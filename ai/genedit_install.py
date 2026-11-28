#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""下载/检查本地生成式修图运行时与模型。

用法：
  python genedit_install.py check <output.json>
  python genedit_install.py install <output.json>

下载内容：
  - stable-diffusion.cpp Windows CUDA12 sd-cli + cudart
  - FLUX.2 Klein 4B Q4_0（diffusion model）
  - Qwen3-4B Q4_K_M（text encoder）
  - FLUX.2 VAE（flux2-vae.safetensors）

所有文件都可放到自定义目录：
  IBT_GENEDIT_RUNTIME_DIR、IBT_GENEDIT_MODEL_DIR
"""
import json
import os
import shutil
import sys
import time
import urllib.request
import zipfile

BASE = os.path.dirname(os.path.abspath(__file__))
RUNTIME_DIR = os.environ.get("IBT_GENEDIT_RUNTIME_DIR") or os.path.join(BASE, "genedit")
MODEL_DIR = os.environ.get("IBT_GENEDIT_MODEL_DIR") or os.path.join(BASE, "models", "genedit")

RUNTIME_ITEMS = [
    {
        "key": "sd_cli",
        "name": "stable-diffusion.cpp CUDA12",
        "url": "https://github.com/leejet/stable-diffusion.cpp/releases/download/master-1106-532d2c1/sd-master-532d2c1-bin-win-cuda12-x64.zip",
        "sha256": "b087219145a382cc1b1e826318289e1f672b9bb6965977734ef0deb85fe2d03a",
        "size": 412365497,
        "zip": True,
        "dest": RUNTIME_DIR,
    },
    {
        "key": "cudart",
        "name": "CUDA 12 runtime",
        "url": "https://github.com/leejet/stable-diffusion.cpp/releases/download/master-1106-532d2c1/cudart-sd-bin-win-cu12-x64.zip",
        "sha256": "fe20366827d357c00797eebb58244dddab7fd9a348d70090c3871004c320f38d",
        "size": 563452046,
        "zip": True,
        "dest": RUNTIME_DIR,
    },
]

MODEL_ITEMS = [
    {
        "key": "diffusion",
        "name": "FLUX.2 Klein 4B Q4_0",
        "url": "https://huggingface.co/leejet/FLUX.2-klein-4B-GGUF/resolve/main/flux-2-klein-4b-Q4_0.gguf?download=true",
        "size": 0,
        "min_size": 2200 * 1024 * 1024,
        "zip": False,
        "dest": os.path.join(MODEL_DIR, "flux-2-klein-4b-Q4_0.gguf"),
    },
    {
        "key": "text_encoder",
        "name": "Qwen3-4B Q4_K_M",
        "url": "https://huggingface.co/unsloth/Qwen3-4B-GGUF/resolve/main/Qwen3-4B-Q4_K_M.gguf?download=true",
        "size": 0,
        "min_size": 2200 * 1024 * 1024,
        "zip": False,
        "dest": os.path.join(MODEL_DIR, "Qwen3-4B-Q4_K_M.gguf"),
    },
    {
        "key": "vae",
        "name": "FLUX.2 VAE",
        "url": "https://huggingface.co/Comfy-Org/Flux-2-Klein-4B/resolve/main/split_files/vae/flux2-vae.safetensors?download=true",
        "size": 0,
        "min_size": 280 * 1024 * 1024,
        "zip": False,
        "dest": os.path.join(MODEL_DIR, "flux2-vae.safetensors"),
    },
]


def _write_json(path, obj):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(obj, f, ensure_ascii=False, indent=2)


def _emit(phase, pct, detail, out=None):
    pct = max(0.0, min(100.0, float(pct or 0)))
    line = "PROGRESS|%s|%.1f|%s" % (phase, pct, (detail or "").replace("|", " "))
    print(line, flush=True)
    if out:
        try:
            _write_json(out, {"ok": True, "phase": phase, "percent": round(pct, 1),
                              "detail": detail, "done": False})
        except Exception:
            pass


def _sha256(path):
    import hashlib
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while True:
            b = f.read(1024 * 1024)
            if not b:
                break
            h.update(b)
    return h.hexdigest()


def _download(url, dest, phase, expected_size=0, sha256="", out=None, base_pct=0.0, span_pct=100.0):
    def _g(local_pct, detail):
        _emit(phase, base_pct + max(0.0, min(100.0, local_pct)) * span_pct / 100.0, detail, out)
    os.makedirs(os.path.dirname(os.path.abspath(dest)), exist_ok=True)
    if os.path.isfile(dest):
        size = os.path.getsize(dest)
        if (expected_size and size >= expected_size * 0.98) or (not expected_size and size > 10 * 1024 * 1024):
            if sha256:
                _g(100, "校验已有文件…")
                if _sha256(dest).lower() == sha256.lower():
                    return True
            else:
                return True
        try:
            os.remove(dest)
        except Exception:
            pass

    part = dest + ".part"
    existing = os.path.getsize(part) if os.path.isfile(part) else 0
    req = urllib.request.Request(url, headers={"User-Agent": "ImageBatchTool/2.28"})
    if existing > 0:
        req.add_header("Range", "bytes=%d-" % existing)
    try:
        resp = urllib.request.urlopen(req, timeout=30)
    except Exception:
        if existing > 0:
            # 服务器不支持断点续传，从头来
            try:
                os.remove(part)
            except Exception:
                pass
        resp = urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "ImageBatchTool/2.28"}), timeout=30)

    status = getattr(resp, "status", 200)
    total = 0
    try:
        total = int(resp.headers.get("Content-Length") or 0)
    except Exception:
        total = 0
    if status == 206:
        total += existing
        mode = "ab"
    else:
        existing = 0
        mode = "wb"
    if expected_size and not total:
        total = expected_size

    last_emit = 0.0
    done = existing
    with open(part, mode) as f:
        while True:
            chunk = resp.read(1024 * 1024)
            if not chunk:
                break
            f.write(chunk)
            done += len(chunk)
            now = time.time()
            if now - last_emit > 1.0:
                pct = (done * 100.0 / total) if total else 0
                _g(min(99.5, pct), "%s / %s MB" % (round(done/1048576, 1), round(total/1048576, 1) if total else "?"))
                last_emit = now
    try:
        resp.close()
    except Exception:
        pass
    os.replace(part, dest)
    _g(100, "下载完成")
    if sha256:
        _g(100, "校验 SHA256…")
        if _sha256(dest).lower() != sha256.lower():
            raise RuntimeError("SHA256 校验失败：" + dest)
    return True


def _extract_zip(path, dest):
    os.makedirs(dest, exist_ok=True)
    with zipfile.ZipFile(path, "r") as z:
        z.extractall(dest)


def _installed(item):
    if item.get("zip"):
        if item["key"] == "sd_cli":
            return os.path.isfile(os.path.join(item["dest"], "sd-cli.exe"))
        # cudart 包装完后是 cudart64_*.dll / cublas*.dll 等
        try:
            names = os.listdir(item["dest"])
            return any(n.lower().startswith("cudart") and n.lower().endswith(".dll") for n in names)
        except Exception:
            return False
    p = item["dest"]
    if not os.path.isfile(p):
        return False
    return os.path.getsize(p) >= item.get("min_size", 1)


def _check():
    items = []
    for it in RUNTIME_ITEMS + MODEL_ITEMS:
        items.append({
            "key": it["key"], "name": it["name"], "dest": it["dest"],
            "installed": _installed(it), "size": os.path.getsize(it["dest"]) if os.path.isfile(it["dest"]) else 0,
        })
    missing = [x for x in items if not x["installed"]]
    estimate = 0
    for it in RUNTIME_ITEMS + MODEL_ITEMS:
        if not _installed(it):
            estimate += it.get("size") or it.get("min_size") or 0
    return {
        "ok": len(missing) == 0,
        "runtime_dir": RUNTIME_DIR,
        "model_dir": MODEL_DIR,
        "items": items,
        "missing": missing,
        "total_missing_bytes": estimate,
    }


def _disk_free():
    try:
        return shutil.disk_usage(BASE).free
    except Exception:
        return 0


def install(out=None):
    os.makedirs(RUNTIME_DIR, exist_ok=True)
    os.makedirs(MODEL_DIR, exist_ok=True)
    if _disk_free() < 7 * 1024 * 1024 * 1024:
        return {"ok": False, "error": "磁盘剩余空间不足 7GB，请先清理再下载。"}

    total = 0
    # 只下载缺失项；已存在则跳过。
    todo = []
    for it in RUNTIME_ITEMS + MODEL_ITEMS:
        if _installed(it):
            continue
        todo.append(it)
        total += it.get("size") or it.get("min_size") or 0
    if not todo:
        return {"ok": True, "already": True, "runtime_dir": RUNTIME_DIR, "model_dir": MODEL_DIR}

    done_bytes = 0
    for it in todo:
        item_size = it.get("size") or it.get("min_size") or 0
        base_pct = (done_bytes / total * 100) if total else 0
        span_pct = (item_size / total * 100) if total else 100
        _emit(it["key"], base_pct, "开始下载 " + it["name"], out)
        try:
            if it.get("zip"):
                zip_path = os.path.join(it["dest"], "_pkg_" + it["key"] + ".zip")
                _download(it["url"], zip_path, it["key"], it.get("size") or 0, it.get("sha256", ""), out, base_pct, span_pct)
                _emit(it["key"], base_pct + span_pct * 0.99, "解压 " + it["name"], out)
                _extract_zip(zip_path, it["dest"])
                try:
                    os.remove(zip_path)
                except Exception:
                    pass
            else:
                _download(it["url"], it["dest"], it["key"], it.get("size") or 0, it.get("sha256", ""), out, base_pct, span_pct)
            done_bytes += item_size
        except Exception as ex:
            return {"ok": False, "error": it["name"] + " 下载失败：" + str(ex), "key": it["key"]}
    return {"ok": True, "runtime_dir": RUNTIME_DIR, "model_dir": MODEL_DIR}


def main():
    if len(sys.argv) < 2:
        print("usage: genedit_install.py check|install <output.json>")
        return 2
    cmd = sys.argv[1].lower()
    out = sys.argv[2] if len(sys.argv) > 2 else ""
    if cmd == "check":
        res = _check()
    elif cmd == "install":
        res = install(out or None)
    else:
        print("unknown command")
        return 2
    if out:
        _write_json(out, res)
    else:
        print(json.dumps(res, ensure_ascii=False, indent=2))
    return 0 if res.get("ok") else 1


if __name__ == "__main__":
    sys.exit(main())
