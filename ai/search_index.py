import argparse
import json
import os
import sqlite3
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import numpy as np
import cv2

from clip_common import set_model_dir, create_session, BertTokenizer, preprocess_image, l2norm
from ocr_common import OcrEngine
from yolo_common import YoloDetector, objects_text
from tag_common import TAG_PROMPTS, build_tags, tags_have_sources

_ROOT_AVAIL_CACHE = {}


def emit(obj):
    print(json.dumps(obj, ensure_ascii=False, separators=(",", ":")), flush=True)


def open_db(path):
    parent = os.path.dirname(os.path.abspath(path))
    if parent:
        os.makedirs(parent, exist_ok=True)
    conn = sqlite3.connect(path, timeout=30)
    conn.execute("PRAGMA journal_mode=WAL")
    conn.execute("PRAGMA synchronous=NORMAL")
    conn.execute("CREATE TABLE IF NOT EXISTS items(path TEXT PRIMARY KEY, mtime INTEGER, size INTEGER, type TEXT, thumb TEXT, emb BLOB, ocr TEXT, obj TEXT, objects TEXT, tags TEXT, tag_text TEXT)")
    # 兼容旧索引表：逐列在线补齐，不丢已有 CLIP 向量、OCR 文本和物体检测结果。
    for column in ("ocr", "obj", "objects", "tags", "tag_text"):
        try:
            conn.execute("ALTER TABLE items ADD COLUMN %s TEXT" % column)
        except Exception:
            pass
    conn.execute("CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT)")
    conn.commit()
    return conn


def load_tag_embeddings(model_dir, gpu=True, threads=4):
    """加载零样本场景标签的文本向量；模型缺失时返回 None，不影响物体/OCR 标签。"""
    txt_model = os.path.join(model_dir, "cnclip_vitb16.txt.fp32.onnx")
    vocab = os.path.join(model_dir, "cn_vocab.txt")
    if not os.path.isfile(txt_model) or not os.path.isfile(vocab):
        return None
    tokenizer = BertTokenizer(vocab)
    txt_sess, _ = create_session(txt_model, gpu=gpu, threads=threads)
    ids = np.array([tokenizer.encode(p) for p in TAG_PROMPTS], dtype=np.int64)
    try:
        out = txt_sess.run(None, {"texts": ids})[0]
    except Exception:
        # 个别 ONNX Runtime 版本对动态 batch 支持不好，退回逐条编码。
        rows = []
        for p in TAG_PROMPTS:
            one = np.array([tokenizer.encode(p)], dtype=np.int64)
            rows.append(txt_sess.run(None, {"texts": one})[0][0])
        out = np.stack(rows, axis=0)
    mat = np.asarray(out, dtype=np.float32)
    if mat.ndim != 2 or mat.shape[0] != len(TAG_PROMPTS):
        return None
    return l2norm(mat)


def read_manifest(path):
    with open(path, "r", encoding="utf-8") as f:
        data = json.load(f)
    if isinstance(data, dict):
        data = data.get("items", [])
    return data or []


def read_priority(path):
    try:
        if not path or not os.path.isfile(path):
            return ""
        with open(path, "r", encoding="utf-8") as f:
            return f.read().strip()
    except Exception:
        return ""


def first_frame(path):
    cap = cv2.VideoCapture(path)
    try:
        ok, frame = cap.read()
        if ok:
            return frame
    finally:
        cap.release()
    return None


def load_image(path, typ, thumb):
    p = thumb if thumb and os.path.isfile(thumb) else path
    if not os.path.isfile(p):
        return None
    data = np.fromfile(p, dtype=np.uint8)
    im = cv2.imdecode(data, cv2.IMREAD_COLOR)
    if im is not None:
        return im
    if typ == "video":
        return first_frame(p)
    return None


def clean_ocr_text(text):
    if not text:
        return ""
    # 去掉过多空白，保留可搜索的中文/英文/数字片段。
    return " ".join(str(text).replace("\r", " ").replace("\n", " ").split())[:4000]


def path_alive(p):
    # 完整索引结束时用真实文件状态兜底清理，避免外部删除后旧条目留在库里。
    try:
        return bool(p) and os.path.isfile(p)
    except Exception:
        return False


def path_root_available(p):
    # 区分“文件真的被删了”和“外接盘/网络盘暂时离线”：
    # 盘符或 UNC 共享根都不可达时，保留索引，等盘恢复后继续可用。
    try:
        drive, _ = os.path.splitdrive(p or "")
        if drive:
            root = drive if drive.startswith("\\\\") else drive + os.sep
        else:
            root = os.path.abspath(os.sep)
        key = root.lower()
        if key in _ROOT_AVAIL_CACHE:
            return _ROOT_AVAIL_CACHE[key]
        value = os.path.exists(root)
        _ROOT_AVAIL_CACHE[key] = value
        return value
    except Exception:
        return False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--manifest", required=True)
    ap.add_argument("--db", required=True)
    ap.add_argument("--model-dir", required=True)
    ap.add_argument("--ocr-dir", default="")
    ap.add_argument("--yolo-dir", default="")
    ap.add_argument("--stop", default="")
    ap.add_argument("--gpu", default="1")
    ap.add_argument("--perf", default="mid", choices=("low", "mid", "high"))
    ap.add_argument("--partial", default="0")
    ap.add_argument("--priority", default="")
    args = ap.parse_args()

    cpu_count = max(1, int(os.cpu_count() or 1))
    perf = args.perf
    if perf == "low":
        threads = 1
        gpu = False
    elif perf == "high":
        threads = cpu_count
        gpu = args.gpu != "0"
    else:
        threads = max(2, min(4, cpu_count))
        gpu = args.gpu != "0"
    # OpenCV 的 resize/decode 是索引的主要 CPU 来源之一；低挡只给 1 线程。
    try:
        cv2.setNumThreads(threads)
    except Exception:
        pass
    for env_name in ("OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS"):
        try:
            os.environ[env_name] = str(threads)
        except Exception:
            pass

    set_model_dir(args.model_dir)
    items = read_manifest(args.manifest)
    total = len(items)
    conn = open_db(args.db)
    meta = dict(conn.execute("SELECT key, value FROM meta").fetchall())
    if meta.get("model") != "cnclip-vitb16-fp32":
        conn.execute("DELETE FROM items")
        conn.commit()
    conn.execute("INSERT OR REPLACE INTO meta(key,value) VALUES('model','cnclip-vitb16-fp32')")
    conn.commit()

    existing = {}
    for p, mt, sz, emb, ocr_text, obj_text, objects_json, tags_json, tag_text in conn.execute(
            "SELECT path, mtime, size, emb, ocr, obj, objects, tags, tag_text FROM items"):
        existing[p] = (mt, sz, emb, ocr_text, obj_text, objects_json, tags_json, tag_text)

    # 续跑进度：manifest 中已经缓存过 CLIP 向量的条目数量。
    # UI 从该数量开始显示，而不是每次重启都从 0/total 开始。
    resume_count = 0
    for rec in items:
        p = rec.get("p") or ""
        old = existing.get(p)
        if old is not None and old[0] == int(rec.get("mt") or 0) and old[1] == int(rec.get("sz") or 0) and old[2]:
            resume_count += 1

    img_model = os.path.join(args.model_dir, "cnclip_vitb16.img.fp32.onnx")
    txt_model = os.path.join(args.model_dir, "cnclip_vitb16.txt.fp32.onnx")
    vocab = os.path.join(args.model_dir, "cn_vocab.txt")
    if not os.path.isfile(img_model):
        emit({"type": "index", "phase": "unavailable", "reason": "image_model_missing", "done": 0, "total": total})
        return 2
    if not os.path.isfile(txt_model) or not os.path.isfile(vocab):
        emit({"type": "index", "phase": "unavailable", "reason": "text_model_missing", "done": 0, "total": total})
        return 2

    ocr_dir = args.ocr_dir or os.path.join(os.path.dirname(os.path.abspath(args.model_dir)), "ocr")
    emit({"type": "index", "phase": "load", "total": total, "done": resume_count, "perf": perf, "threads": threads, "gpu": 1 if gpu else 0})
    sess, providers = create_session(img_model, gpu=gpu, threads=threads)

    # OCR 失败不阻塞 CLIP 索引；模型缺失时搜索仍可做纯画面语义检索。
    ocr = None
    try:
        ocr = OcrEngine(ocr_dir, gpu=gpu, threads=threads)
    except Exception:
        ocr = None
    ocr_ok = bool(ocr is not None and ocr.ok)

    # YOLOX 物体检测失败也不阻塞 CLIP/OCR；旧库中 obj 为 NULL 的条目会保留，
    # 未来模型可用时再补做。
    yolo_dir = args.yolo_dir or os.path.join(os.path.dirname(os.path.abspath(args.model_dir)), "yolox")
    yolo = None
    try:
        yolo = YoloDetector(yolo_dir, gpu=gpu, threads=threads)
    except Exception:
        yolo = None
    yolo_ok = bool(yolo is not None and yolo.ok)

    # 多标签场景向量：同一张图可以同时命中多个零样本标签。
    # 文本模型缺失时只保留 YOLO/OCR 标签，不影响索引继续。
    tag_emb = None
    try:
        # 标签文本模型固定用 CPU：图片/OCR/YOLO 已经占用 DirectML 时，
        # 再开第二个 DML session 在部分显卡/驱动上会直接崩溃。
        tag_emb = load_tag_embeddings(args.model_dir, gpu=False, threads=threads)
    except Exception:
        tag_emb = None
    tags_ok = tag_emb is not None

    emit({"type": "index", "phase": "start", "total": total, "done": resume_count,
          "providers": providers, "ocr": 1 if ocr_ok else 0, "obj": 1 if yolo_ok else 0,
          "tags": 1 if tags_ok else 0,
          "perf": perf, "threads": threads, "gpu": 1 if gpu else 0})

    added = 0
    ocr_added = 0
    obj_added = 0
    skipped = 0
    failed = 0
    last_emit = time.time()
    stop = False
    priority_path = ""

    for idx in range(len(items)):
        if args.stop and os.path.exists(args.stop):
            stop = True
            break
        rec = items[idx]
        # 详情页刚点开的图片优先：把它的 manifest 记录提到当前队首。
        if args.priority:
            wanted = read_priority(args.priority)
            if wanted and wanted != priority_path:
                priority_path = wanted
                if (rec.get("p") or "") != wanted:
                    for j in range(idx + 1, len(items)):
                        if (items[j].get("p") or "") == wanted:
                            items[idx], items[j] = items[j], items[idx]
                            rec = items[idx]
                            break
        p = rec.get("p") or ""
        mt = int(rec.get("mt") or 0)
        sz = int(rec.get("sz") or 0)
        typ = rec.get("t") or ""
        thumb = rec.get("thumb") or ""
        if not p:
            failed += 1
            continue
        old = existing.get(p)
        same = old is not None and old[0] == mt and old[1] == sz
        need_emb = (not same) or (old is not None and not old[2])
        # OCR 只处理静态图片/实况/GIF；视频先不抽字幕，避免每张视频首帧重复做 OCR。
        ocr_capable = typ in ("photo", "live", "gif", "")
        need_ocr = ocr_ok and ocr_capable and (not same or (old is not None and old[3] is None))
        # YOLOX 对照片/视频/动图都可用；RAW 交给原有 CLIP/OCR 路径，避免解码失败。
        obj_capable = typ in ("photo", "live", "gif", "video", "")
        need_obj = yolo_ok and obj_capable and (not same or (old is not None and old[4] is None))
        # 旧索引没有 tags/tag_text 时补算；旧格式缺少来源标记时也重算一次，
        # 保证每张图都能区分 YOLO/CLIP/OCR 三套标签。已有完整来源标签的条目不再重复处理。
        need_tags = (old is None) or (old[6] is None) or (old[7] is None) or (old[6] and not tags_have_sources(old[6]))
        if same and not need_emb and not need_ocr and not need_obj and not need_tags:
            skipped += 1
        else:
            try:
                # 有缩略图时 CLIP 优先读缩略图；OCR/YOLO 必须用原图/原视频帧，
                # 避免小图文字和物体细节丢失。
                thumb_im = None
                if need_emb:
                    thumb_path = thumb if thumb and os.path.isfile(thumb) else ""
                    thumb_im = load_image(p, typ, thumb_path)
                im = None
                if need_ocr or need_obj:
                    im = load_image(p, typ, "")
                if im is None and need_emb and thumb_im is not None:
                    im = thumb_im

                emb_vec = None
                if need_emb and im is not None:
                    x = preprocess_image_from_bgr(thumb_im if thumb_im is not None else im)
                    raw = sess.run(None, {"image": x})[0]
                    emb_vec = l2norm(raw)[0].astype(np.float32)
                    y = emb_vec.astype(np.float16).tobytes()
                    added += 1
                elif need_emb:
                    raise RuntimeError("image_unavailable")
                elif same and old is not None and old[2]:
                    emb_vec = np.frombuffer(old[2], dtype=np.float16).astype(np.float32)
                    emb_vec = emb_vec / (np.linalg.norm(emb_vec) + 1e-12)

                if need_ocr:
                    text = clean_ocr_text(ocr.read_bgr(im) if im is not None else "")
                    # 空字符串也写回，表示“已处理但无文字”，避免下次重启重复 OCR。
                    ocr_added += 1
                else:
                    text = None

                dets = []
                if need_obj:
                    if im is None:
                        raise RuntimeError("object_image_unavailable")
                    dets = yolo.detect_bgr(im)
                    obj_added += 1

                old_emb = old[2] if (same and old is not None and old[2]) else None
                if need_emb:
                    emb = y
                else:
                    emb = old_emb
                if need_ocr:
                    ocr_text = text
                elif same and old is not None and old[3] is not None:
                    ocr_text = old[3]
                else:
                    # OCR 引擎不可用时写 NULL，下次模型可用后会补做；
                    # 引擎可用但视频等不做 OCR 的类型写空串，表示已处理。
                    ocr_text = None if not ocr_ok else ""

                if need_obj:
                    obj_text = objects_text(dets)
                    objects_json = json.dumps(dets, ensure_ascii=False, separators=(",", ":"))
                elif same and old is not None and old[4] is not None:
                    obj_text = old[4]
                    objects_json = old[5]
                else:
                    # YOLO 模型不可用时写 NULL，未来模型可用后会补做；
                    # 模型可用但没检测到物体写空串，表示已处理。
                    obj_text = None if not yolo_ok else ""
                    objects_json = None if not yolo_ok else "[]"

                if need_tags:
                    tag_ocr_text = ocr_text if ocr_text is not None else (old[3] if old is not None else "")
                    tag_obj_text = obj_text if obj_text is not None else (old[4] if old is not None else "")
                    tag_objects_json = objects_json if objects_json is not None else (old[5] if old is not None else "")
                    tag_dets = dets if need_obj else None
                    tags_list, tag_text_value = build_tags(
                        image_emb=emb_vec,
                        label_emb=tag_emb,
                        detections=tag_dets,
                        objects_json=tag_objects_json,
                        obj_text=tag_obj_text,
                        ocr_text=tag_ocr_text,
                    )
                    tags_json = json.dumps(tags_list, ensure_ascii=False, separators=(",", ":"))
                else:
                    tags_json = old[6] if old is not None else "[]"
                    tag_text_value = old[7] if old is not None else ""

                conn.execute(
                    "INSERT OR REPLACE INTO items(path,mtime,size,type,thumb,emb,ocr,obj,objects,tags,tag_text) VALUES(?,?,?,?,?,?,?,?,?,?,?)",
                    (p, mt, sz, typ, thumb, emb, ocr_text, obj_text, objects_json, tags_json, tag_text_value))
            except Exception:
                failed += 1
            if (idx + 1) % 10 == 0:
                conn.commit()
        if (idx + 1) % 10 == 0 or (time.time() - last_emit) >= 1.0:
            conn.commit()
            display_done = max(resume_count, idx + 1)
            emit({"type": "index", "phase": "run", "done": display_done, "total": total,
                  "added": added, "ocr_added": ocr_added, "obj_added": obj_added,
                  "skipped": skipped, "failed": failed,
                  "ocr": 1 if ocr_ok else 0, "obj": 1 if yolo_ok else 0, "tags": 1 if tags_ok else 0,
                  "providers": providers})
            last_emit = time.time()

    conn.commit()
    # 文件被删除/移走后，旧索引项不再保留，避免搜索返回“幽灵照片”。
    # 只有完整跑完一轮才清理；扫描中的部分索引和中途 stop 都不动已有索引。
    if not stop and args.partial != "1":
        manifest_paths = set()
        for rec in items:
            p0 = rec.get("p") or ""
            if p0:
                manifest_paths.add(p0)
        # 从当前数据库读取全部条目，包含本轮新增的行，避免新图刚索引完就被删掉时漏清理。
        # 外接盘/网络盘根不可达时先保留，等盘恢复后仍能继续使用索引。
        all_paths = [r[0] for r in conn.execute("SELECT path FROM items").fetchall()]
        stale = []
        for p in all_paths:
            if p in manifest_paths and path_alive(p):
                continue
            if not path_root_available(p):
                continue
            stale.append(p)
        if stale:
            conn.executemany("DELETE FROM items WHERE path=?", [(p,) for p in stale])
            conn.commit()
    count = conn.execute("SELECT COUNT(*) FROM items").fetchone()[0]
    if stop:
        emit({"type": "index", "phase": "stopped", "done": 0, "total": total, "added": added,
              "ocr_added": ocr_added, "obj_added": obj_added, "skipped": skipped, "failed": failed,
              "count": count, "ocr": 1 if ocr_ok else 0, "obj": 1 if yolo_ok else 0,
              "tags": 1 if tags_ok else 0, "providers": providers})
    else:
        emit({"type": "index", "phase": "done", "done": total, "total": total, "added": added,
              "ocr_added": ocr_added, "obj_added": obj_added, "skipped": skipped, "failed": failed,
              "count": count, "ocr": 1 if ocr_ok else 0, "obj": 1 if yolo_ok else 0,
              "tags": 1 if tags_ok else 0, "providers": providers})
    conn.close()
    return 0


def preprocess_image_from_bgr(im):
    h, w = im.shape[:2]
    short = min(h, w)
    if short < 224:
        scale = 224.0 / max(1, short)
        im = cv2.resize(im, (max(224, int(round(w * scale))), max(224, int(round(h * scale)))), interpolation=cv2.INTER_CUBIC)
    elif short > 224:
        scale = 224.0 / short
        im = cv2.resize(im, (max(224, int(round(w * scale))), max(224, int(round(h * scale)))), interpolation=cv2.INTER_CUBIC)
    h, w = im.shape[:2]
    x0 = max(0, (w - 224) // 2)
    y0 = max(0, (h - 224) // 2)
    im = im[y0:y0 + 224, x0:x0 + 224]
    im = cv2.cvtColor(im, cv2.COLOR_BGR2RGB).astype(np.float32) / 255.0
    mean = np.array([0.48145466, 0.4578275, 0.40821073], dtype=np.float32)
    std = np.array([0.26862954, 0.26130258, 0.27577711], dtype=np.float32)
    im = (im - mean) / std
    im = im.transpose(2, 0, 1)
    return np.ascontiguousarray(im[None, ...])


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as e:
        emit({"type": "index", "phase": "error", "reason": str(e)})
        raise
