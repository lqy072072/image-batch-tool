import os
import sys

import numpy as np
import cv2

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from clip_common import create_session, set_model_dir


def _clip(v, lo, hi):
    return max(lo, min(hi, v))


def _order_box(box):
    box = np.asarray(box, dtype=np.float32).reshape(-1, 2)
    s = box.sum(axis=1)
    d = np.diff(box, axis=1).reshape(-1)
    out = np.zeros((4, 2), dtype=np.float32)
    out[0] = box[np.argmin(s)]
    out[2] = box[np.argmax(s)]
    out[1] = box[np.argmin(d)]
    out[3] = box[np.argmax(d)]
    return out


def _box_score(prob, box):
    h, w = prob.shape[:2]
    xs = np.clip(box[:, 0], 0, w - 1).astype(np.int32)
    ys = np.clip(box[:, 1], 0, h - 1).astype(np.int32)
    xmin, xmax = int(xs.min()), int(xs.max())
    ymin, ymax = int(ys.min()), int(ys.max())
    if xmax <= xmin or ymax <= ymin:
        return 0.0
    return float(prob[ymin:ymax + 1, xmin:xmax + 1].mean())


def _detect_boxes(prob, scale_x, scale_y, thresh=0.28, box_thresh=0.45, unclip_ratio=1.7, max_candidates=1000):
    mask = (prob > thresh).astype(np.uint8) * 255
    contours, _ = cv2.findContours(mask, cv2.RETR_LIST, cv2.CHAIN_APPROX_SIMPLE)
    boxes = []
    for cnt in contours[:max_candidates]:
        if cnt is None or len(cnt) < 4:
            continue
        area = float(cv2.contourArea(cnt))
        if area < 3:
            continue
        rect = cv2.minAreaRect(cnt)
        (cx, cy), (rw, rh), ang = rect
        if rw < 2 or rh < 2:
            continue
        box = _order_box(cv2.boxPoints(rect))
        score = _box_score(prob, box)
        if score < box_thresh:
            continue
        # DB 的 unclip：按文字框短边向外扩一圈，避免把边缘笔画裁掉。
        # minAreaRect 的 (rw, rh) 顺序会随旋转角度变化，不能把长边当高度来扩。
        pad = max(2.0, min(rw, rh) * (unclip_ratio - 1.0) * 0.5)
        rw2, rh2 = rw + pad * 2, rh + pad * 2
        rect2 = ((cx, cy), (rw2, rh2), ang)
        box = _order_box(cv2.boxPoints(rect2))
        box[:, 0] = np.clip(box[:, 0] * scale_x, 0, (prob.shape[1] * scale_x) - 1)
        box[:, 1] = np.clip(box[:, 1] * scale_y, 0, (prob.shape[0] * scale_y) - 1)
        w = float(np.linalg.norm(box[0] - box[1]))
        h = float(np.linalg.norm(box[1] - box[2]))
        if w < 4 or h < 4:
            continue
        if max(w / max(h, 1e-6), h / max(w, 1e-6)) > 22:
            continue
        boxes.append((box, score))
    return boxes


def _crop_box(im, box):
    box = _order_box(box)
    w = int(max(np.linalg.norm(box[0] - box[1]), np.linalg.norm(box[2] - box[3])))
    h = int(max(np.linalg.norm(box[1] - box[2]), np.linalg.norm(box[0] - box[3])))
    if w < 4 or h < 4:
        return None
    dst = np.array([[0, 0], [w - 1, 0], [w - 1, h - 1], [0, h - 1]], dtype=np.float32)
    m = cv2.getPerspectiveTransform(box.astype(np.float32), dst)
    crop = cv2.warpPerspective(im, m, (w, h), flags=cv2.INTER_CUBIC, borderMode=cv2.BORDER_REPLICATE)
    if crop is None or crop.size == 0:
        return None
    # 竖排文本先转成横排，识别模型主要按横向文本训练。
    if crop.shape[0] > crop.shape[1] * 1.8:
        crop = cv2.rotate(crop, cv2.ROTATE_90_CLOCKWISE)
    return crop


def _sort_boxes(boxes):
    if not boxes:
        return []
    boxes = sorted(boxes, key=lambda x: (float(x[0][:, 1].mean()), float(x[0][:, 0].mean())))
    rows = []
    for b, s in boxes:
        cy = float(b[:, 1].mean())
        placed = False
        for row in rows:
            ref = row[0][0]
            if abs(cy - float(ref[:, 1].mean())) <= max(12.0, 0.55 * max(1.0, float(ref[:, 1].max() - ref[:, 1].min()))):
                row.append((b, s))
                placed = True
                break
        if not placed:
            rows.append([(b, s)])
    out = []
    for row in rows:
        row = sorted(row, key=lambda x: float(x[0][:, 0].mean()))
        out.extend(row)
    return out


class OcrEngine:
    """PP-OCRv4 中文检测 + 识别。模型缺失或 DirectML 不支持时自动回退/降级。"""

    def __init__(self, model_dir, gpu=True, det_limit=1280, threads=2):
        self.model_dir = os.path.abspath(model_dir)
        self.det_limit = int(det_limit)
        self.threads = max(1, int(threads))
        self.det = None
        self.rec = None
        self.chars = []
        self.ok = False
        self.providers = []
        self.gpu = bool(gpu)
        self._load()

    def _load(self):
        det_path = os.path.join(self.model_dir, "det.onnx")
        rec_path = os.path.join(self.model_dir, "rec.onnx")
        dict_path = os.path.join(self.model_dir, "ppocr_keys_v1.txt")
        if not (os.path.isfile(det_path) and os.path.isfile(rec_path) and os.path.isfile(dict_path)):
            return
        try:
            # OCR 与 CLIP 共用同一份 DirectML onnxruntime 运行库。
            if self.gpu:
                cnclip_dir = os.path.join(os.path.dirname(self.model_dir), "cnclip")
                if os.path.isdir(os.path.join(cnclip_dir, "dml")):
                    set_model_dir(cnclip_dir)
            self.det, p1 = create_session(det_path, gpu=self.gpu, threads=self.threads)
            self.rec, p2 = create_session(rec_path, gpu=self.gpu, threads=self.threads)
            self.providers = p1 + ["/"] + p2
            with open(dict_path, "r", encoding="utf-8") as f:
                lines = [line.rstrip("\n").rstrip("\r") for line in f]
            # PP-OCR 的 CTC 类别：blank + 字典 + 空格
            self.chars = [""] + lines + [" "]
            self.ok = True
        except Exception:
            self.ok = False

    def _fallback_cpu(self):
        try:
            det_path = os.path.join(self.model_dir, "det.onnx")
            rec_path = os.path.join(self.model_dir, "rec.onnx")
            self.det, _ = create_session(det_path, gpu=False, threads=self.threads)
            self.rec, _ = create_session(rec_path, gpu=False, threads=self.threads)
            self.providers = ["CPUExecutionProvider"]
            self.ok = True
            return True
        except Exception:
            self.ok = False
            return False

    def _det_preprocess(self, im):
        h, w = im.shape[:2]
        ratio = min(1.0, float(self.det_limit) / max(h, w))
        nh = max(32, int(round(h * ratio / 32.0)) * 32)
        nw = max(32, int(round(w * ratio / 32.0)) * 32)
        resized = cv2.resize(im, (nw, nh), interpolation=cv2.INTER_LINEAR)
        rgb = cv2.cvtColor(resized, cv2.COLOR_BGR2RGB).astype(np.float32) / 255.0
        mean = np.array([0.485, 0.456, 0.406], dtype=np.float32)
        std = np.array([0.229, 0.224, 0.225], dtype=np.float32)
        rgb = (rgb - mean) / std
        rgb = rgb.transpose(2, 0, 1)
        return np.ascontiguousarray(rgb[None, ...]), w / float(nw), h / float(nh)

    def _rec_preprocess(self, crop):
        h, w = crop.shape[:2]
        target_h = 48
        target_w = int(round(w * (target_h / float(max(h, 1)))))
        target_w = _clip(target_w, 16, 640)
        crop = cv2.resize(crop, (target_w, target_h), interpolation=cv2.INTER_LINEAR)
        rgb = cv2.cvtColor(crop, cv2.COLOR_BGR2RGB).astype(np.float32) / 255.0
        rgb = (rgb - 0.5) / 0.5
        rgb = rgb.transpose(2, 0, 1)
        return np.ascontiguousarray(rgb[None, ...])

    def _ctc_decode(self, pred):
        if pred is None or len(pred.shape) < 2:
            return "", 0.0
        ids = np.argmax(pred, axis=2)[0]
        probs = np.max(pred, axis=2)[0]
        chars = []
        confs = []
        prev = -1
        for idx, conf in zip(ids.tolist(), probs.tolist()):
            idx = int(idx)
            if idx == prev:
                prev = idx
                continue
            prev = idx
            if idx == 0:
                continue
            if 0 <= idx < len(self.chars):
                ch = self.chars[idx]
                if ch:
                    chars.append(ch)
                    confs.append(float(conf))
        text = "".join(chars).strip()
        conf = float(np.mean(confs)) if confs else 0.0
        return text, conf

    def _recognize_crop(self, crop):
        x = self._rec_preprocess(crop)
        # rec 的宽高是动态维；部分 DML 版本对动态 shape 支持不好。
        try:
            pred = self.rec.run(None, {"x": x})[0]
        except Exception:
            if self.gpu and self._fallback_cpu():
                pred = self.rec.run(None, {"x": x})[0]
            else:
                raise
        return self._ctc_decode(pred)

    def read_bgr(self, im, min_conf=0.35):
        if not self.ok or im is None:
            return ""
        try:
            x, sx, sy = self._det_preprocess(im)
            try:
                prob = self.det.run(None, {"x": x})[0][0, 0]
            except Exception:
                if self.gpu and self._fallback_cpu():
                    prob = self.det.run(None, {"x": x})[0][0, 0]
                else:
                    raise
            boxes = _detect_boxes(prob, sx, sy)
            boxes = _sort_boxes(boxes)
            lines = []
            for box, _score in boxes:
                crop = _crop_box(im, box)
                if crop is None:
                    continue
                text, conf = self._recognize_crop(crop)
                if not text:
                    continue
                # 低置信度的短碎片往往是背景纹理，丢弃；长文本放宽。
                if conf < min_conf and len(text) < 4:
                    continue
                lines.append(text)
            return " ".join(lines)
        except Exception:
            return ""

    def read_path(self, path, min_conf=0.35):
        if not self.ok:
            return ""
        try:
            data = np.fromfile(path, dtype=np.uint8)
            im = cv2.imdecode(data, cv2.IMREAD_COLOR)
            if im is None:
                return ""
            return self.read_bgr(im, min_conf=min_conf)
        except Exception:
            return ""
