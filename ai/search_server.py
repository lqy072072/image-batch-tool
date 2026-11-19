import argparse
import json
import os
import sqlite3
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import numpy as np

from clip_common import set_model_dir, create_session, BertTokenizer, l2norm


def emit(obj):
    sys.stdout.write(json.dumps(obj, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def load_index(db_path):
    conn = sqlite3.connect(db_path, timeout=30)
    conn.execute("PRAGMA query_only=ON")
    rows = conn.execute("SELECT path, emb FROM items").fetchall()
    conn.close()
    paths = []
    vecs = []
    dim = None
    for p, b in rows:
        if not b:
            continue
        a = np.frombuffer(b, dtype=np.float16).astype(np.float32)
        if a.size == 0:
            continue
        if dim is None:
            dim = a.size
        if a.size != dim:
            continue
        paths.append(p)
        vecs.append(a)
    if not vecs:
        return paths, np.zeros((0, dim or 512), dtype=np.float32)
    mat = np.stack(vecs).astype(np.float32)
    mat = l2norm(mat)
    return paths, mat


def open_text_db(db_path):
    try:
        conn = sqlite3.connect(db_path, timeout=30)
        conn.execute("PRAGMA query_only=ON")
        cols = [r[1] for r in conn.execute("PRAGMA table_info(items)").fetchall()]
        if "ocr" not in cols:
            conn.close()
            return None
        return conn
    except Exception:
        return None


def escape_like(s):
    return (s or "").replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_")


def path_alive(p):
    # 搜索返回前逐条确认文件仍在，避免外部删除后仍显示“幽灵照片”。
    try:
        return bool(p) and os.path.isfile(p)
    except Exception:
        return False


def ocr_search(conn, q, limit):
    if conn is None:
        return []
    try:
        tokens = [t for t in (q or "").split() if t]
        if not tokens:
            return []
        # 多词用 AND 收敛；中文查询通常只有一个 token。
        sql = "SELECT path FROM items WHERE ocr IS NOT NULL AND ocr != ''"
        params = []
        for t in tokens[:4]:
            sql += " AND ocr LIKE ? ESCAPE '\\'"
            params.append("%" + escape_like(t) + "%")
        # 先多取一些候选再过滤，避免已删除文件的记录占掉前 N 个名额。
        fetch = max(1, min(4000, int(limit) * 4))
        sql += " LIMIT " + str(fetch)
        rows = conn.execute(sql, params).fetchall()
        return [r[0] for r in rows if r and r[0] and path_alive(r[0])]
    except Exception:
        return []


def obj_search(conn, q, limit):
    if conn is None:
        return []
    try:
        tokens = [t for t in (q or "").split() if t]
        if not tokens:
            return []
        sql = "SELECT path FROM items WHERE obj IS NOT NULL AND obj != ''"
        params = []
        for t in tokens[:4]:
            sql += " AND obj LIKE ? ESCAPE '\\'"
            params.append("%" + escape_like(t) + "%")
        # 先多取一些候选再过滤，避免已删除文件的记录占掉前 N 个名额。
        fetch = max(1, min(4000, int(limit) * 4))
        sql += " LIMIT " + str(fetch)
        rows = conn.execute(sql, params).fetchall()
        return [r[0] for r in rows if r and r[0] and path_alive(r[0])]
    except Exception:
        return []


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", required=True)
    ap.add_argument("--model-dir", required=True)
    ap.add_argument("--gpu", default="1")
    args = ap.parse_args()

    set_model_dir(args.model_dir)
    txt_model = os.path.join(args.model_dir, "cnclip_vitb16.txt.fp32.onnx")
    vocab_path = os.path.join(args.model_dir, "cn_vocab.txt")
    if not os.path.isfile(txt_model) or not os.path.isfile(vocab_path):
        emit({"type": "ready", "ok": False, "reason": "model_missing"})
        return 2
    if not os.path.isfile(args.db):
        emit({"type": "ready", "ok": False, "reason": "index_missing"})
        return 2

    tokenizer = BertTokenizer(vocab_path)
    sess, providers = create_session(txt_model, gpu=(args.gpu != "0"), threads=4)
    paths, mat = load_index(args.db)
    text_conn = open_text_db(args.db)
    obj_cols = []
    try:
        obj_cols = [r[1] for r in text_conn.execute("PRAGMA table_info(items)").fetchall()] if text_conn else []
    except Exception:
        obj_cols = []
    obj_ready = 1 if "obj" in obj_cols else 0
    emit({"type": "ready", "ok": True, "count": len(paths), "dim": int(mat.shape[1]) if mat.size else 0,
          "providers": providers, "ocr": 1 if text_conn is not None else 0, "obj": obj_ready})

    query_cache = {}
    max_cache = 32
    for raw in sys.stdin:
        line = raw.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except Exception:
            continue
        cmd = msg.get("cmd")
        if cmd == "ping":
            emit({"type": "pong"})
            continue
        if cmd == "reload":
            try:
                paths, mat = load_index(args.db)
                text_conn = open_text_db(args.db)
                query_cache.clear()
                obj_ready = 0
                try:
                    obj_ready = 1 if text_conn and "obj" in [r[1] for r in text_conn.execute("PRAGMA table_info(items)").fetchall()] else 0
                except Exception:
                    obj_ready = 0
                emit({"type": "reloaded", "count": len(paths), "dim": int(mat.shape[1]) if mat.size else 0,
                      "ocr": 1 if text_conn is not None else 0, "obj": obj_ready})
            except Exception as e:
                emit({"type": "error", "reason": "reload_failed:" + str(e)})
            continue
        if cmd == "status":
            emit({"type": "status", "count": len(paths), "dim": int(mat.shape[1]) if mat.size else 0,
                  "ocr": 1 if text_conn is not None else 0, "obj": obj_ready})
            continue
        if cmd != "search":
            emit({"type": "error", "reason": "unknown_cmd"})
            continue
        q = (msg.get("q") or "").strip()
        limit = int(msg.get("limit") or 500)
        if not q:
            emit({"type": "result", "q": q, "paths": [], "scores": []})
            continue
        try:
            if q in query_cache:
                cached = query_cache[q]
                cached_paths = []
                cached_scores = []
                for i, p in enumerate(cached[0]):
                    if path_alive(p):
                        cached_paths.append(p)
                        cached_scores.append(cached[1][i])
                query_cache[q] = (cached_paths, cached_scores, cached[2], cached[3], cached[4])
                emit({"type": "result", "q": q, "paths": cached_paths, "scores": cached_scores,
                      "count": cached[2], "ocr_count": cached[3], "obj_count": cached[4]})
                continue
            ids = np.array([tokenizer.encode(q)], dtype=np.int64)
            z = sess.run(None, {"texts": ids})[0]
            z = l2norm(z)[0]
            ocr_paths = ocr_search(text_conn, q, limit)
            obj_paths = obj_search(text_conn, q, limit)
            out_paths = []
            out_scores = []
            seen = set()
            # OCR 文字精确命中优先，其次是 YOLO 物体标签，最后接 CLIP 语义相似结果。
            for p in ocr_paths:
                if p and p not in seen and path_alive(p):
                    seen.add(p)
                    out_paths.append(p)
                    out_scores.append(1.0)
            for p in obj_paths:
                if p and p not in seen and path_alive(p):
                    seen.add(p)
                    out_paths.append(p)
                    out_scores.append(0.95)
            if len(out_paths) < limit and len(paths) > 0:
                scores = mat @ z
                # 先取比目标更多的候选；如果过滤掉已删除文件后不足 limit，
                # 再扩大候选范围补齐，避免返回结果无故变少。
                take = min(len(paths), max(1, limit * 2))
                while True:
                    n = max(1, take)
                    idx = np.argpartition(-scores, n - 1)[:n]
                    idx = idx[np.argsort(-scores[idx])]
                    for i in idx:
                        p = paths[int(i)]
                        if p in seen:
                            continue
                        if not path_alive(p):
                            continue
                        seen.add(p)
                        out_paths.append(p)
                        out_scores.append(round(float(scores[int(i)]), 5))
                        if len(out_paths) >= limit:
                            break
                    if len(out_paths) >= limit or n >= len(paths):
                        break
                    take = min(len(paths), n * 2)
            # 限制返回数量，OCR/物体命中已经放在最前。
            out_paths = out_paths[:limit]
            out_scores = out_scores[:limit]
            if len(query_cache) >= max_cache:
                query_cache.pop(next(iter(query_cache)))
            query_cache[q] = (out_paths, out_scores, len(paths), len(ocr_paths), len(obj_paths))
            emit({"type": "result", "q": q, "paths": out_paths, "scores": out_scores,
                  "count": len(paths), "ocr_count": len(ocr_paths), "obj_count": len(obj_paths)})
        except Exception as e:
            emit({"type": "error", "reason": "search_failed:" + str(e)})
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as e:
        emit({"type": "error", "reason": str(e)})
        raise
