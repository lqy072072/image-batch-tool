# -*- coding: utf-8 -*-
"""
相册 - 人脸分类引擎（支持增量与断点状态）
用法：python face_group.py <图片路径列表文件(UTF-8, 每行一个)> [状态文件]

状态文件保存已分析图片的脸部特征和分组，下次只处理新增/变化的图片；
旧的组仍保留，新增人脸优先归入已有组。
"""
import base64
import json
import os
import sys

import cv2
import numpy as np

SIM_THRESHOLD = 0.36
MAX_SIDE = 1400
CONF = 0.65
STATE_VERSION = 2


def log_err(msg):
    sys.stdout.write("E\t%s\n" % str(msg).replace("\t", " ").replace("\n", " "))
    sys.stdout.flush()


def imread_unicode(path):
    try:
        data = np.fromfile(path, dtype=np.uint8)
        if data.size == 0:
            return None
        return cv2.imdecode(data, cv2.IMREAD_COLOR)
    except Exception:
        return None


def ascii_model_path(path):
    try:
        path.encode("ascii")
        return path
    except UnicodeEncodeError:
        pass
    import shutil
    import tempfile
    cache = os.path.join(tempfile.gettempdir(), "ibt_face_models")
    if not os.path.isdir(cache):
        os.makedirs(cache)
    dst = os.path.join(cache, os.path.basename(path))
    try:
        if (not os.path.exists(dst)) or os.path.getsize(dst) != os.path.getsize(path):
            shutil.copy2(path, dst)
    except Exception:
        return path
    return dst


def norm_feat(values):
    try:
        f = np.array(values, dtype=np.float32).reshape(-1)
        n = float(np.linalg.norm(f))
        if n < 1e-6:
            return None
        return f / n
    except Exception:
        return None


def load_state(path):
    try:
        if path and os.path.exists(path):
            with open(path, "r", encoding="utf-8-sig") as f:
                obj = json.load(f)
            if isinstance(obj, dict):
                return obj
    except Exception as e:
        log_err("读取人脸状态失败: %s" % e)
    return {}


def save_state(path, images, thumbs):
    if not path:
        return
    try:
        state = {
            "version": STATE_VERSION,
            "images": images,
            "thumbs": thumbs,
        }
        os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
        tmp = path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(state, f, ensure_ascii=False, separators=(",", ":"))
        os.replace(tmp, path)
    except Exception as e:
        log_err("保存人脸状态失败: %s" % e)


def main():
    if len(sys.argv) < 2:
        log_err("缺少参数：图片列表文件")
        return 2
    list_file = sys.argv[1]
    state_file = sys.argv[2] if len(sys.argv) > 2 else ""
    stop_file = sys.argv[3] if len(sys.argv) > 3 else ""
    if not os.path.exists(list_file):
        log_err("列表文件不存在")
        return 2

    base = os.path.dirname(os.path.abspath(__file__))
    det_model = os.path.join(base, "models", "face_detection_yunet.onnx")
    rec_model = os.path.join(base, "models", "face_recognition_sface.onnx")
    if not os.path.exists(det_model):
        log_err("缺少 YuNet 模型 ai/models/face_detection_yunet.onnx")
        return 3
    if not os.path.exists(rec_model):
        log_err("缺少 SFace 模型 ai/models/face_recognition_sface.onnx")
        return 3

    with open(list_file, "r", encoding="utf-8-sig") as f:
        paths = [ln.strip() for ln in f if ln.strip()]

    state = load_state(state_file)
    images = state.get("images", {}) if isinstance(state.get("images", {}), dict) else {}
    thumbs = state.get("thumbs", {}) if isinstance(state.get("thumbs", {}), dict) else {}

    # 清理旧版本/损坏的条目，重建每组的特征和数量。
    group_sum = {}
    group_count = {}
    max_gid = -1
    bad = []
    for path, rec in list(images.items()):
        if not isinstance(rec, dict) or not isinstance(rec.get("faces"), list):
            bad.append(path)
            continue
        for face in rec.get("faces", []):
            try:
                feat = norm_feat(face.get("feat"))
                gid = int(face.get("gid"))
            except Exception:
                feat = None
            if feat is None:
                bad.append(path)
                continue
            if gid < 0:
                continue
            if gid not in group_sum:
                group_sum[gid] = np.zeros(128, dtype=np.float32)
                group_count[gid] = 0
            group_sum[gid] += feat
            group_count[gid] += 1
            max_gid = max(max_gid, gid)
    for path in bad:
        images.pop(path, None)

    def centroid(gid):
        s = group_sum.get(gid)
        if s is None:
            return None
        n = float(np.linalg.norm(s))
        if n < 1e-6:
            return None
        return s / n

    def remove_path(path):
        rec = images.pop(path, None)
        if not rec:
            return
        for face in rec.get("faces", []):
            try:
                feat = norm_feat(face.get("feat"))
                gid = int(face.get("gid"))
            except Exception:
                continue
            if feat is None or gid not in group_sum:
                continue
            group_sum[gid] -= feat
            group_count[gid] = group_count.get(gid, 0) - 1
            if group_count[gid] <= 0:
                group_sum.pop(gid, None)
                group_count.pop(gid, None)

    def assign(feat):
        nonlocal max_gid
        best_gid, best_sim = -1, -1.0
        for gid in list(group_sum.keys()):
            c = centroid(gid)
            if c is None:
                continue
            s = float(np.dot(c, feat))
            if s > best_sim:
                best_sim, best_gid = s, gid
        if best_gid >= 0 and best_sim >= SIM_THRESHOLD:
            group_sum[best_gid] += feat
            group_count[best_gid] += 1
            return best_gid, best_sim
        gid = max_gid + 1
        max_gid = gid
        group_sum[gid] = feat.copy()
        group_count[gid] = 1
        return gid, 1.0

    # 只处理新增或大小/修改时间变化的图片，旧图片不再重新检测。
    todo = []
    valid = set(paths)
    for path in paths:
        try:
            st = os.stat(path)
            size, mtime = int(st.st_size), int(st.st_mtime_ns)
        except Exception:
            continue
        old = images.get(path)
        if old and int(old.get("size", -1)) == size and int(old.get("mtime", -1)) == mtime:
            # 兼容旧版状态：没有 done 字段的旧条目视为整轮已完成。
            if old.get("done", True):
                continue
        if old:
            remove_path(path)
        images[path] = {"size": size, "mtime": mtime, "faces": [], "done": False}
        todo.append(path)

    # 删除已经不在当前相册清单中的旧图片。
    for path in list(images.keys()):
        if path not in valid:
            remove_path(path)

    total = len(todo)
    album_total = len(paths)
    processed_before = 0
    for path in paths:
        rec = images.get(path)
        if rec and rec.get("done", True):
            processed_before += 1

    detector = cv2.FaceDetectorYN_create(ascii_model_path(det_model), "", (640, 640), CONF, 0.3, 1000)
    recognizer = cv2.FaceRecognizerSF_create(ascii_model_path(rec_model), "")

    global_max = max(group_sum.keys()) if group_sum else -1
    done = 0
    if total > 0:
        sys.stdout.write("P\t%d\t%d\n" % (processed_before, album_total))
        sys.stdout.flush()
    for path in todo:
        # 用户点“停止分析”或程序准备退出时，从当前断点保存后立刻退出；
        # 下次启动只处理剩余项，不会重新从第一张开始。
        if stop_file and os.path.exists(stop_file):
            save_state(state_file, images, thumbs)
            sys.stdout.write("P\t%d\t%d\n" % (processed_before + done, album_total))
            sys.stdout.flush()
            return 0
        done += 1
        if done % 5 == 0 or done == total:
            sys.stdout.write("P\t%d\t%d\n" % (processed_before + done, album_total))
            sys.stdout.flush()

        try:
            img = imread_unicode(path)
            if img is None:
                continue
            h0, w0 = img.shape[:2]
            scale = 1.0
            if max(h0, w0) > MAX_SIDE:
                scale = MAX_SIDE / float(max(h0, w0))
                img = cv2.resize(img, (int(w0 * scale), int(h0 * scale)), interpolation=cv2.INTER_AREA)
            h, w = img.shape[:2]
            if w < 40 or h < 40:
                continue
            detector.setInputSize((w, h))
            try:
                _, faces = detector.detect(img)
            except Exception as e:
                log_err("检测失败 %s: %s" % (os.path.basename(path), e))
                continue
            if faces is None:
                continue

            for row in faces:
                score = float(row[14])
                if score < CONF:
                    continue
                x, y, bw, bh = [float(v) for v in row[0:4]]
                x = max(0.0, x); y = max(0.0, y)
                bw = min(bw, w - x); bh = min(bh, h - y)
                if bw < 24 or bh < 24:
                    continue
                try:
                    aligned = recognizer.alignCrop(img, row)
                    feat = recognizer.feature(aligned)
                    feat = norm_feat(feat)
                    if feat is None:
                        continue
                except Exception:
                    continue

                gid, sim = assign(feat)
                ox, oy, ow, oh = int(x / scale), int(y / scale), int(bw / scale), int(bh / scale)
                images[path]["faces"].append({
                    "gid": int(gid),
                    "x": ox, "y": oy, "w": ow, "h": oh,
                    "feat": [round(float(v), 6) for v in feat.tolist()],
                })

                old = thumbs.get(str(gid))
                if old is None or score > float(old.get("score", -1)):
                    try:
                        thumb = cv2.resize(aligned, (96, 96), interpolation=cv2.INTER_AREA)
                        ok, buf = cv2.imencode(".jpg", thumb, [int(cv2.IMWRITE_JPEG_QUALITY), 82])
                        if ok:
                            thumbs[str(gid)] = {
                                "score": round(score, 6),
                                "b64": base64.b64encode(buf.tobytes()).decode("ascii"),
                            }
                    except Exception:
                        pass
        except Exception as e:
            log_err("分析失败 %s: %s" % (os.path.basename(path), e))
        finally:
            rec = images.get(path)
            if rec is not None:
                rec["done"] = True
            # 每 25 张落一次盘；中途被关闭/杀掉后下次从这里继续，
            # 最多只需重做 24 张，避免 12 万张相册被长时间阻塞在写状态上。
            if done % 25 == 0:
                save_state(state_file, images, thumbs)

    # 输出全部已有分组/人脸，界面可以完整替换。
    for path in paths:
        rec = images.get(path)
        if not rec:
            continue
        for face in rec.get("faces", []):
            sys.stdout.write("F\t%d\t%s\t%d\t%d\t%d\t%d\n" % (
                int(face.get("gid", 0)), path,
                int(face.get("x", 0)), int(face.get("y", 0)),
                int(face.get("w", 0)), int(face.get("h", 0))))
            sys.stdout.flush()

    for gid in sorted(thumbs.keys(), key=lambda s: int(s)):
        t = thumbs.get(gid)
        if isinstance(t, dict) and t.get("b64"):
            sys.stdout.write("G\t%d\t%s\n" % (int(gid), t["b64"]))
            sys.stdout.flush()

    sys.stdout.write("P\t%d\t%d\n" % (album_total, album_total))
    sys.stdout.flush()
    save_state(state_file, images, thumbs)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as e:
        log_err("异常退出: %s" % e)
        sys.exit(1)
