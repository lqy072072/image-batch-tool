"""Lightweight YOLOX-Tiny object detector (ONNX Runtime, Apache-2.0 model).

The model predicts COCO-80 objects.  It is intentionally small enough to ship
inside the installer and reuse the existing DirectML runtime from clip_common.
"""

import os

import cv2
from imgio import imread_u, imwrite_u
import numpy as np

from clip_common import create_session

COCO_LABELS = [
    "person", "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat",
    "traffic light", "fire hydrant", "stop sign", "parking meter", "bench", "bird", "cat",
    "dog", "horse", "sheep", "cow", "elephant", "bear", "zebra", "giraffe", "backpack",
    "umbrella", "handbag", "tie", "suitcase", "frisbee", "skis", "snowboard", "sports ball",
    "kite", "baseball bat", "baseball glove", "skateboard", "surfboard", "tennis racket",
    "bottle", "wine glass", "cup", "fork", "knife", "spoon", "bowl", "banana", "apple",
    "sandwich", "orange", "broccoli", "carrot", "cake", "chair", "couch", "potted plant",
    "bed", "dining table", "toilet", "tv", "laptop", "mouse", "remote", "keyboard",
    "cell phone", "microwave", "oven", "toaster", "sink", "refrigerator", "book", "clock",
    "vase", "scissors", "teddy bear", "hair drier", "toothbrush",
]

# Chinese aliases make the COCO labels searchable with the same language used in
# the album UI.  Multiple aliases are intentional (e.g. 汽车 / 车 / 轿车).
LABEL_ALIASES = {
    "person": "人 人物 行人",
    "bicycle": "自行车 单车",
    "car": "汽车 车 轿车",
    "motorcycle": "摩托车 摩托",
    "airplane": "飞机",
    "bus": "公交车 巴士",
    "train": "火车",
    "truck": "卡车 货车",
    "boat": "船 小船",
    "traffic light": "交通灯 红绿灯",
    "fire hydrant": "消防栓",
    "stop sign": "停止标志",
    "parking meter": "停车计时器",
    "bench": "长椅 板凳",
    "bird": "鸟 小鸟",
    "cat": "猫 小猫",
    "dog": "狗 小狗",
    "horse": "马",
    "sheep": "羊 绵羊",
    "cow": "牛",
    "elephant": "大象",
    "bear": "熊",
    "zebra": "斑马",
    "giraffe": "长颈鹿",
    "backpack": "背包 书包",
    "umbrella": "雨伞 伞",
    "handbag": "手提包 包",
    "tie": "领带",
    "suitcase": "行李箱 箱子",
    "frisbee": "飞盘",
    "skis": "滑雪板",
    "snowboard": "单板滑雪板",
    "sports ball": "球 运动球",
    "kite": "风筝",
    "baseball bat": "棒球棒",
    "baseball glove": "棒球手套",
    "skateboard": "滑板",
    "surfboard": "冲浪板",
    "tennis racket": "网球拍",
    "bottle": "瓶子 瓶",
    "wine glass": "酒杯",
    "cup": "杯子 杯",
    "fork": "叉子",
    "knife": "刀",
    "spoon": "勺子",
    "bowl": "碗",
    "banana": "香蕉",
    "apple": "苹果",
    "sandwich": "三明治",
    "orange": "橙子 橘子",
    "broccoli": "西兰花",
    "carrot": "胡萝卜",
    "cake": "蛋糕",
    "chair": "椅子 凳子",
    "couch": "沙发",
    "potted plant": "植物 盆栽 绿植",
    "bed": "床",
    "dining table": "餐桌 桌子 饭桌",
    "toilet": "马桶 厕所",
    "tv": "电视 电视机",
    "laptop": "笔记本电脑 电脑",
    "mouse": "鼠标",
    "remote": "遥控器",
    "keyboard": "键盘",
    "cell phone": "手机 电话",
    "microwave": "微波炉",
    "oven": "烤箱",
    "toaster": "烤面包机",
    "sink": "水槽",
    "refrigerator": "冰箱",
    "book": "书 书本 书籍",
    "clock": "时钟 钟",
    "vase": "花瓶",
    "scissors": "剪刀",
    "teddy bear": "泰迪熊 玩偶 毛绒玩具",
    "hair drier": "吹风机",
    "toothbrush": "牙刷",
}


def aliases_for(label):
    return LABEL_ALIASES.get(label, "")


def objects_text(detections, max_labels=30):
    """Build the searchable object string stored in SQLite."""
    parts = []
    seen = set()
    for d in detections:
        label = d.get("l") or ""
        if not label:
            continue
        candidates = [label] + aliases_for(label).split()
        for item in candidates:
            item = item.strip().lower()
            if not item or item in seen:
                continue
            seen.add(item)
            parts.append(item)
            if len(parts) >= max_labels:
                return " ".join(parts)
    return " ".join(parts)


class YoloDetector:
    def __init__(self, model_dir, gpu=True, threads=4, size=416):
        self.size = int(size)
        self.model_path = os.path.join(model_dir, "yolox_tiny.onnx")
        if not os.path.isfile(self.model_path):
            raise RuntimeError("yolox_model_missing")
        self.sess, self.providers = create_session(self.model_path, gpu=gpu, threads=threads)
        self.input_name = self.sess.get_inputs()[0].name
        self.ok = True

    def _letterbox(self, im):
        h, w = im.shape[:2]
        scale = min(float(self.size) / max(1, w), float(self.size) / max(1, h))
        nw = max(1, int(round(w * scale)))
        nh = max(1, int(round(h * scale)))
        resized = cv2.resize(im, (nw, nh), interpolation=cv2.INTER_LINEAR)
        padded = np.full((self.size, self.size, 3), 114, dtype=np.uint8)
        padded[:nh, :nw] = resized
        # YOLOX's ONNX export expects the OpenCV BGR uint8 range (0-255); it
        # performs normalization inside the graph.
        x = padded.transpose(2, 0, 1)[None, ...].astype(np.float32)
        return np.ascontiguousarray(x), scale, nw, nh

    def _decode(self, raw):
        """Decode YOLOX raw output (the exported model keeps raw grid offsets)."""
        out = np.asarray(raw, dtype=np.float32)[0]
        grids = []
        strides = []
        for stride in (8, 16, 32):
            h = self.size // stride
            w = self.size // stride
            grid = np.stack(np.meshgrid(np.arange(w), np.arange(h)), axis=2).reshape(-1, 2)
            grids.append(grid)
            strides.append(np.full((h * w, 1), stride, dtype=np.float32))
        if sum(len(g) for g in grids) != out.shape[0]:
            # Defensive fallback for a differently exported model: if it is already
            # decoded, leave the boxes untouched.
            return out
        grid = np.concatenate(grids, axis=0)
        stride = np.concatenate(strides, axis=0)
        box = out.copy()
        box[:, :2] = (box[:, :2] + grid) * stride
        box[:, 2:4] = np.exp(np.clip(box[:, 2:4], -8.0, 8.0)) * stride
        # Objectness/class probabilities are already sigmoided in this export.
        box[:, 4:] = np.clip(box[:, 4:], 0.0, 1.0)
        return box

    def detect_bgr(self, im, score_thr=0.30, nms_thr=0.45, max_det=30):
        if im is None or im.size == 0:
            return []
        h, w = im.shape[:2]
        x, scale, nw, nh = self._letterbox(im)
        raw = self.sess.run(None, {self.input_name: x})[0]
        out = self._decode(raw)
        if out is None or len(out) == 0:
            return []
        cls_scores = out[:, 5:]
        cls_ids = np.argmax(cls_scores, axis=1)
        conf = out[np.arange(out.shape[0]), 4] * cls_scores[np.arange(out.shape[0]), cls_ids]
        # Keep low-cost prefilter before per-class NMS.
        keep = np.where(conf >= score_thr)[0]
        if len(keep) == 0:
            return []
        boxes = out[keep, :4].copy()
        scores = conf[keep].astype(np.float32)
        classes = cls_ids[keep].astype(np.int32)
        boxes[:, 0] -= boxes[:, 2] / 2.0
        boxes[:, 1] -= boxes[:, 3] / 2.0
        # Convert letterboxed coordinates back to the original image.
        boxes[:, 0] = (boxes[:, 0] - 0.0) / max(scale, 1e-6)
        boxes[:, 1] = (boxes[:, 1] - 0.0) / max(scale, 1e-6)
        boxes[:, 2] = boxes[:, 2] / max(scale, 1e-6)
        boxes[:, 3] = boxes[:, 3] / max(scale, 1e-6)
        dets = []
        # Per-class NMS; class-agnostic NMS would suppress overlapping pet/person boxes.
        for c in np.unique(classes):
            inds = np.where(classes == c)[0]
            if len(inds) == 0:
                continue
            rects = []
            for i in inds:
                bx, by, bw, bh = boxes[i]
                bx = max(0.0, min(float(w - 1), float(bx)))
                by = max(0.0, min(float(h - 1), float(by)))
                bw = max(0.0, min(float(w - bx), float(bw)))
                bh = max(0.0, min(float(h - by), float(bh)))
                if bw < 2 or bh < 2:
                    continue
                rects.append((i, [bx, by, bw, bh], float(scores[i])))
            if not rects:
                continue
            nms_boxes = [r[1] for r in rects]
            nms_scores = [r[2] for r in rects]
            try:
                nms = cv2.dnn.NMSBoxes(nms_boxes, nms_scores, score_thr, nms_thr)
            except Exception:
                nms = np.arange(len(rects))
            if len(nms) == 0:
                continue
            for j in np.array(nms).reshape(-1):
                idx, rect, score = rects[int(j)]
                cid = int(c)
                label = COCO_LABELS[cid] if 0 <= cid < len(COCO_LABELS) else str(cid)
                dets.append({
                    "l": label,
                    "c": round(float(score), 4),
                    "x": round(float(rect[0]), 1),
                    "y": round(float(rect[1]), 1),
                    "w": round(float(rect[2]), 1),
                    "h": round(float(rect[3]), 1),
                })
        dets.sort(key=lambda d: d["c"], reverse=True)
        return dets[:max_det]

    def detect_path(self, path, **kwargs):
        if not path or not os.path.isfile(path):
            return []
        data = np.fromfile(path, dtype=np.uint8)
        im = cv2.imdecode(data, cv2.IMREAD_COLOR)
        if im is None:
            return []
        return self.detect_bgr(im, **kwargs)
