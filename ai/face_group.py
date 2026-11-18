# -*- coding: utf-8 -*-
"""
相册 - 人脸分类引擎
用法：python face_group.py <图片路径列表文件(UTF-8, 每行一个)>

算法：
  1) 检测：OpenCV YuNet（face_detection_yunet.onnx），比 Haar 稳，带 5 点关键点
  2) 对齐 + 特征：OpenCV SFace（face_recognition_sface.onnx，ArcFace 系 128 维），
     官方同一人余弦阈值约 0.363
  3) 聚类：按余弦相似度贪心归组（取最近组质心，质心随成员更新），
     再按组内人脸数排序输出

输出行协议（\t 分隔）：
  P\t<done>\t<total>            进度
  F\t<gid>\t<path>\tx\ty\tw\th   某人脸所在照片与框（原图像素坐标）
  G\t<gid>\t<base64 jpeg>       组封面（对齐后的人脸缩略图）
  E\t<msg>                      非致命错误
"""
import base64
import os
import sys

import cv2
import numpy as np

SIM_THRESHOLD = 0.36      # SFace 余弦阈值（官方 0.363，略放宽提升召回）
MAX_SIDE = 1400           # 检测前缩放上限，兼顾速度与小人脸召回
CONF = 0.65


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
    """OpenCV 的 dnn 导入器在含中文的路径上会读不到 onnx，这里复制到纯 ASCII 的临时目录再加载。"""
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


def main():
    if len(sys.argv) < 2:
        log_err("缺少参数：图片列表文件")
        return 2
    list_file = sys.argv[1]
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
    total = len(paths)

    detector = cv2.FaceDetectorYN_create(ascii_model_path(det_model), "", (640, 640), CONF, 0.3, 1000)
    recognizer = cv2.FaceRecognizerSF_create(ascii_model_path(rec_model), "")

    groups = []      # {"centroid": np.ndarray(128), "count": int, "thumb": (score, b64)}
    thumbs = {}      # gid -> (score, b64)

    def assign(feat):
        best_gid, best_sim = -1, -1.0
        for gid, g in enumerate(groups):
            s = float(np.dot(g["centroid"], feat))
            if s > best_sim:
                best_sim, best_gid = s, gid
        if best_gid >= 0 and best_sim >= SIM_THRESHOLD:
            g = groups[best_gid]
            n = g["count"]
            c = (g["centroid"] * n + feat) / (n + 1)
            nrm = np.linalg.norm(c)
            g["centroid"] = c / nrm if nrm > 1e-6 else feat
            g["count"] = n + 1
            return best_gid, best_sim
        groups.append({"centroid": feat.copy(), "count": 1})
        return len(groups) - 1, 1.0

    done = 0
    faces_total = 0
    for path in paths:
        done += 1
        if done % 5 == 0 or done == total:
            sys.stdout.write("P\t%d\t%d\n" % (done, total))
            sys.stdout.flush()

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
                feat = np.array(feat, dtype=np.float32).reshape(-1)
                nrm = np.linalg.norm(feat)
                if nrm < 1e-6:
                    continue
                feat = feat / nrm
            except Exception:
                continue

            gid, sim = assign(feat)
            faces_total += 1
            ox, oy, ow, oh = int(x / scale), int(y / scale), int(bw / scale), int(bh / scale)
            sys.stdout.write("F\t%d\t%s\t%d\t%d\t%d\t%d\n" % (gid, path, ox, oy, ow, oh))
            sys.stdout.flush()

            old = thumbs.get(gid)
            if old is None or score > old[0]:
                try:
                    thumb = cv2.resize(aligned, (96, 96), interpolation=cv2.INTER_AREA)
                    ok, buf = cv2.imencode(".jpg", thumb, [int(cv2.IMWRITE_JPEG_QUALITY), 82])
                    if ok:
                        thumbs[gid] = (score, base64.b64encode(buf.tobytes()).decode("ascii"))
                except Exception:
                    pass

    for gid in sorted(thumbs.keys()):
        sys.stdout.write("G\t%d\t%s\n" % (gid, thumbs[gid][1]))
        sys.stdout.flush()

    sys.stdout.write("P\t%d\t%d\n" % (total, total))
    sys.stdout.flush()
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as e:
        log_err("异常退出: %s" % e)
        sys.exit(1)
