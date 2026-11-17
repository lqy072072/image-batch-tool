#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
美颜相机式分项美颜（纯 onnxruntime + opencv）

区域层：BiSeNet(face-parsing ResNet18) 19 类语义分割 → 皮肤/唇/眼/眉/脖/发
几何层：MediaPipe FaceMesh 468 关键点 → 瘦脸/大眼/瘦鼻/下巴
质感层：导向/双边滤波磨皮、Lab 美白、斑点局部修补、口红、亮眼、黑眼圈
增强层：可选 GFPGAN v1.4 细节修复（默认关闭）

用法:
  python beauty_pro.py <输入> <输出> [key=value[,key=value...]]
参数(0-100，可用小数):
  smooth 磨皮  white 美白  spot 祛斑  dark 祛黑眼圈  eye 亮眼
  lip 口红   lipcolor 0-5   rosy 红润   slim 瘦脸  eyesize 大眼
  nose 瘦鼻  chin -100~100（正值下巴变长）  aifix 0/1  aifix_strength 0-100
"""
import sys, os, json, time
import numpy as np
import cv2
import onnxruntime as ort

BASE = os.path.dirname(os.path.abspath(__file__))
MODEL_DIR = os.path.join(BASE, "models")
BISENET_PATH = os.path.join(MODEL_DIR, "bisenet_resnet18.onnx")
MESH_PATH = os.path.join(MODEL_DIR, "face_mesh_468.onnx")
YUNET_PATH = os.path.join(MODEL_DIR, "face_detection_yunet.onnx")
GFPGAN_PATH = os.path.join(MODEL_DIR, "GFPGANv1.4.onnx")

# 口红色号（OpenCV H 0-180, S, V）
LIP_COLORS = [
    ("正红", 0, 210, 215),
    ("豆沙", 172, 110, 180),
    ("珊瑚", 10, 165, 225),
    ("蜜桃", 16, 105, 235),
    ("玫红", 160, 175, 205),
    ("裸橘", 13, 130, 230),
]

_sessions = {}


def get_session(path):
    if path not in _sessions:
        opts = ort.SessionOptions()
        opts.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        opts.intra_op_num_threads = 2
        _sessions[path] = ort.InferenceSession(path, sess_options=opts,
                                               providers=["CPUExecutionProvider"])
    return _sessions[path]


# ---------------------------------------------------------------- 人脸检测
def detect_faces(img):
    """YuNet 检测，返回 [(cx, cy, fw, score)]（原图坐标）"""
    h, w = img.shape[:2]
    det = get_session(YUNET_PATH)
    input_size = 640
    scale = min(input_size / w, input_size / h)
    new_w, new_h = int(w * scale), int(h * scale)
    resized = cv2.resize(img, (new_w, new_h))
    pad = np.zeros((input_size, input_size, 3), dtype=np.uint8)
    pad[:new_h, :new_w] = resized
    inp = np.transpose(pad.astype(np.float32), (2, 0, 1))[None, ...]
    outputs = det.run(None, {det.get_inputs()[0].name: inp})
    out_map = {o.name: outputs[i][0] for i, o in enumerate(det.get_outputs())}

    all_dets = []
    for stride in [8, 16, 32]:
        grid = input_size // stride
        cls = out_map["cls_%d" % stride]
        obj = out_map["obj_%d" % stride]
        bbox = out_map["bbox_%d" % stride]
        scores = cls[:, 0] * obj[:, 0]
        idxs = np.where(scores >= 0.6)[0]
        for idx in idxs:
            gy, gx = divmod(int(idx), grid)
            cx = (gx + 0.5 + bbox[idx, 0]) * stride
            cy = (gy + 0.5 + bbox[idx, 1]) * stride
            pred_w = bbox[idx, 2] * stride * 3.5
            all_dets.append((cx / scale, cy / scale, max(pred_w / scale, 40), float(scores[idx])))

    if not all_dets:
        return []
    all_dets.sort(key=lambda d: -d[3])
    keep = []
    for det in all_dets:
        for (kcx, kcy, kfw, _) in keep:
            if ((det[0] - kcx) ** 2 + (det[1] - kcy) ** 2) ** 0.5 < (det[2] + kfw) * 0.35:
                break
        else:
            keep.append(det)
    return keep


# ---------------------------------------------------------------- 区域层
class FaceParser:
    MEAN = np.array([0.485, 0.456, 0.406], np.float32)
    STD = np.array([0.229, 0.224, 0.225], np.float32)
    SIZE = 512

    def __init__(self, path=BISENET_PATH):
        self.session = get_session(path)
        self.input_name = self.session.get_inputs()[0].name

    def parse(self, img):
        rgb = cv2.resize(cv2.cvtColor(img, cv2.COLOR_BGR2RGB), (self.SIZE, self.SIZE),
                         interpolation=cv2.INTER_LINEAR)
        x = (rgb.astype(np.float32) / 255.0 - self.MEAN) / self.STD
        x = np.transpose(x, (2, 0, 1))[None].astype(np.float32)
        out = self.session.run(None, {self.input_name: x})[0]
        m = out.squeeze(0).argmax(0).astype(np.uint8)
        return cv2.resize(m, (img.shape[1], img.shape[0]), interpolation=cv2.INTER_NEAREST)


# ---------------------------------------------------------------- 几何层
class FaceMesh:
    SIZE = 192

    def __init__(self, path=MESH_PATH):
        self.session = get_session(path)
        self.input_name = self.session.get_inputs()[0].name

    def _one_pass(self, img, box, angle):
        x1, y1, x2, y2 = box
        side = 1.5 * max(x2 - x1, y2 - y1)
        cx, cy = (x1 + x2) / 2.0, (y1 + y2) / 2.0
        m = cv2.getRotationMatrix2D((cx, cy), angle, self.SIZE / side)
        m[0, 2] += self.SIZE / 2.0 - cx
        m[1, 2] += self.SIZE / 2.0 - cy
        crop = cv2.warpAffine(img, m, (self.SIZE, self.SIZE))
        inv = cv2.invertAffineTransform(m)
        c = np.transpose(cv2.cvtColor(crop, cv2.COLOR_BGR2RGB).astype(np.float32) / 255.0,
                         (2, 0, 1))[None]
        lm = self.session.run(None, {self.input_name: c})[0].reshape(-1, 3).astype(np.float32)
        return cv2.transform(lm[None, :, :2], inv)[0]

    def predict(self, img, box):
        """两遍：先用正框粗估，再按眼线角度校正，返回 (468,2)"""
        p = self._one_pass(img, box, 0.0)
        ang = float(np.degrees(np.arctan2(p[263][1] - p[33][1], p[263][0] - p[33][0])))
        if abs(ang) > 0.5:
            p = self._one_pass(img, box, ang)
        return p


# ---------------------------------------------------------------- 工具
def _odd(v, lo=3):
    v = int(v)
    if v < lo:
        v = lo
    return v if v % 2 == 1 else v + 1


def feather(mask, ratio=0.012):
    k = _odd(min(mask.shape[:2]) * ratio * 2 + 1, 5)
    return cv2.GaussianBlur(mask.astype(np.float32), (k, k), 0)


def blend(dst, src, w):
    if w.ndim == 2:
        w = w[:, :, None]
    out = dst.astype(np.float32) * (1.0 - w) + src.astype(np.float32) * w
    return np.clip(out, 0, 255).astype(np.uint8)


def softstep(x):
    x = np.clip(x, 0.0, 1.0)
    return x * x * (3.0 - 2.0 * x)


def guided_filter(guide, src, r, eps):
    """自实现导向滤波（无 ximgproc 依赖），guide/src 为 float32 单通道"""
    g = guide.astype(np.float32)
    p = src.astype(np.float32)
    mean_g = cv2.boxFilter(g, -1, (r, r))
    mean_p = cv2.boxFilter(p, -1, (r, r))
    cov = cv2.boxFilter(g * p, -1, (r, r)) - mean_g * mean_p
    var = cv2.boxFilter(g * g, -1, (r, r)) - mean_g * mean_g
    a = cov / (var + eps)
    b = mean_p - a * mean_g
    mean_a = cv2.boxFilter(a, -1, (r, r))
    mean_b = cv2.boxFilter(b, -1, (r, r))
    return mean_a * g + mean_b


# ---------------------------------------------------------------- 质感层
def op_smooth(crop, w, s):
    """磨皮：导向滤波压平皮肤 + 回注一部分高频，避免塑料感"""
    if s <= 0 or w.max() <= 0:
        return crop
    h, wd = crop.shape[:2]
    scale = max(0.28, min(1.0, 700.0 / max(h, wd)))  # 大图降采样处理再上采，速度稳定
    sw, sh = max(16, int(wd * scale)), max(16, int(h * scale))
    small = cv2.resize(crop, (sw, sh), interpolation=cv2.INTER_AREA)

    radius = _odd(min(sw, sh) * 0.045, 5)
    y = cv2.cvtColor(small, cv2.COLOR_BGR2GRAY).astype(np.float32) / 255.0
    eps = 0.004 + 0.02 * (1.0 - s)
    out = np.empty_like(small, np.float32)
    for c in range(3):
        ch = small[:, :, c].astype(np.float32)
        base = guided_filter(y, ch / 255.0, radius, eps) * 255.0
        out[:, :, c] = base
    base = np.clip(out, 0, 255).astype(np.uint8)
    base = cv2.resize(base, (wd, h), interpolation=cv2.INTER_LINEAR)

    detail = crop.astype(np.float32) - base.astype(np.float32)
    detail_gain = 0.45 - 0.20 * s          # 强度越大保留细节越少
    smoothed = np.clip(base.astype(np.float32) + detail * detail_gain, 0, 255).astype(np.uint8)

    ww = w * (0.55 + 0.40 * s)
    return blend(crop, smoothed, ww)


def op_white(crop, w, s):
    """美白：Lab 提亮 L + 轻微去黄偏色，高光软保护"""
    if s <= 0 or w.max() <= 0:
        return crop
    lab = cv2.cvtColor(crop, cv2.COLOR_BGR2LAB).astype(np.float32)
    L = lab[:, :, 0]
    gain = 0.36 * s
    hl_protect = 1.0 - (L / 255.0) ** 3 * 0.75
    newL = L + (255.0 - L) * gain * hl_protect
    lab[:, :, 0] = newL
    lab[:, :, 1] = 128.0 + (lab[:, :, 1] - 128.0) * (1.0 - 0.10 * s)
    lab[:, :, 2] = 128.0 + (lab[:, :, 2] - 128.0) * (1.0 - 0.14 * s)
    out = cv2.cvtColor(np.clip(lab, 0, 255).astype(np.uint8), cv2.COLOR_LAB2BGR)
    return blend(crop, out, w)


def op_spot(crop, w, s, protect):
    """祛斑/祛痘：皮肤内高频暗斑检测 → 局部中值修补 + 轻微提亮"""
    if s <= 0 or w.max() <= 0:
        return crop
    h, wd = crop.shape[:2]
    k = _odd(max(3, min(h, wd) * 0.02), 3)
    base = cv2.medianBlur(crop, k)
    L = cv2.cvtColor(crop, cv2.COLOR_BGR2GRAY).astype(np.float32)
    sigma = max(2.0, min(h, wd) * 0.014)
    bg = cv2.GaussianBlur(L, (0, 0), sigma)
    resp = bg - L                                   # 暗斑处为正
    skin_ok = (w > 0.12) & (protect < 0.35)
    vals = resp[skin_ok]
    thr = max(5.0, 1.8 * float(vals.std()) if vals.size else 8.0)
    spots = ((resp > thr) & skin_ok).astype(np.uint8)
    spots = cv2.dilate(spots, np.ones((3, 3), np.uint8), iterations=1)
    sm = feather(spots.astype(np.float32), 0.008) * s
    lifted = np.clip(base.astype(np.float32) + 6.0 * s, 0, 255).astype(np.uint8)
    out = blend(crop, lifted, np.clip(sm, 0, 1))
    return out


def op_dark_circle(crop, mask, eye_mask, s):
    """祛黑眼圈：眼罩下移 55% 眼高 ∩ 皮肤，局部提亮 + 轻磨"""
    if s <= 0 or eye_mask.max() <= 0:
        return crop
    h, wd = crop.shape[:2]
    ys, xs = np.where(eye_mask > 0)
    if ys.size == 0:
        return crop
    eh = ys.max() - ys.min() + 1
    M = np.float32([[1, 0, 0], [0, 1, eh * 0.55]])
    shifted = cv2.warpAffine(eye_mask, M, (wd, h))
    region = shifted * ((mask == 1).astype(np.float32))
    region = feather(region, 0.010) * s
    area = np.clip(region, 0, 1)
    lab = cv2.cvtColor(crop, cv2.COLOR_BGR2LAB).astype(np.float32)
    L = lab[:, :, 0]
    lab[:, :, 0] = L + (255.0 - L) * 0.22 * area
    out = cv2.cvtColor(np.clip(lab, 0, 255).astype(np.uint8), cv2.COLOR_LAB2BGR)
    return blend(crop, out, area)


def op_eye(crop, eye_mask, s):
    """亮眼：眼区锐化 + 眼白提亮"""
    if s <= 0 or eye_mask.max() <= 0:
        return crop
    ww = feather(eye_mask, 0.006) * (0.75 * s)
    blur = cv2.GaussianBlur(crop, (0, 0), 1.6)
    sharp = np.clip(crop.astype(np.float32) + (crop.astype(np.float32) - blur.astype(np.float32)) * 0.9, 0, 255)
    hsv = cv2.cvtColor(crop, cv2.COLOR_BGR2HSV).astype(np.float32)
    sclera = hsv[:, :, 1] < 60
    hsv[:, :, 2] = np.where(sclera, np.minimum(255.0, hsv[:, :, 2] + 26.0 * s), hsv[:, :, 2])
    bright = cv2.cvtColor(np.clip(hsv, 0, 255).astype(np.uint8), cv2.COLOR_HSV2BGR)
    out = blend(sharp.astype(np.uint8), bright, ww)
    return blend(crop, out, ww)


def op_lip(crop, lip_mask, s, idx):
    """口红：唇部换色相/提饱和，保 V 细节（色相按最短圆弧插值，避免绕到紫色）"""
    if s <= 0 or lip_mask.max() <= 0:
        return crop
    name, th, ts, tv = LIP_COLORS[int(idx) % len(LIP_COLORS)]
    # 分割掩码从 512 上采后有台阶，重羽化保证唇缘自然过渡
    sigma = max(2.5, min(crop.shape[:2]) * 0.006)
    lip_soft = cv2.GaussianBlur(lip_mask.astype(np.float32), (0, 0), sigma)
    ww = np.clip(lip_soft, 0, 1) * (0.92 * s)

    hsv = cv2.cvtColor(crop, cv2.COLOR_BGR2HSV).astype(np.float32)
    h = hsv[:, :, 0]
    k = 0.88 * s
    # OpenCV 色相 0-179 首尾相接：取最短方向位移
    dh = np.mod(th - h + 90.0, 180.0) - 90.0
    h_new = np.mod(h + dh * k, 180.0)
    s_new = hsv[:, :, 1] * (1 - k) + np.maximum(hsv[:, :, 1], ts) * k
    v_new = np.minimum(255.0, hsv[:, :, 2] * (1.0 + 0.05 * s) + tv * 0.02 * s)
    out_hsv = np.stack([h_new, s_new, v_new], axis=2)
    colored = cv2.cvtColor(np.clip(out_hsv, 0, 255).astype(np.uint8), cv2.COLOR_HSV2BGR)
    # V 细节回注，避免唇纹被抹平
    y0 = cv2.cvtColor(crop, cv2.COLOR_BGR2GRAY).astype(np.float32)
    y1 = cv2.cvtColor(colored, cv2.COLOR_BGR2GRAY).astype(np.float32)
    colored = np.clip(colored.astype(np.float32) + (y0 - y1)[:, :, None] * 0.35, 0, 255).astype(np.uint8)
    return blend(crop, colored, ww)


def op_rosy(crop, w, cheek_mask, s):
    """红润：面颊 Lab-a 提升，带轻微亮度"""
    if s <= 0 or cheek_mask.max() <= 0:
        return crop
    ww = feather(cheek_mask, 0.012) * (0.55 * s)
    lab = cv2.cvtColor(crop, cv2.COLOR_BGR2LAB).astype(np.float32)
    lab[:, :, 1] = lab[:, :, 1] + 9.0 * s * ww
    lab[:, :, 0] = lab[:, :, 0] + 3.0 * s * ww
    out = cv2.cvtColor(np.clip(lab, 0, 255).astype(np.uint8), cv2.COLOR_LAB2BGR)
    return blend(crop, out, ww * 0.85)


# ---------------------------------------------------------------- 几何层变形
def _grid(h, wd):
    yy, xx = np.mgrid[0:h, 0:wd].astype(np.float32)
    return xx, yy


def warp_all(crop, pts, p, fw, keep=None):
    """瘦脸 / 大眼 / 瘦鼻 / 下巴 一次性组合成 remap；keep=1 的区域（眼/眉/唇）保持刚性"""
    slim = p.get('slim', 0.0) / 100.0
    eyesize = p.get('eyesize', 0.0) / 100.0
    nose = p.get('nose', 0.0) / 100.0
    chin = p.get('chin', 0.0) / 100.0
    if slim <= 0 and eyesize <= 0 and nose <= 0 and abs(chin) <= 0:
        return crop

    h, wd = crop.shape[:2]
    mapx, mapy = _grid(h, wd)
    base_x, base_y = mapx.copy(), mapy.copy()   # 原始网格，用于边界衰减
    rigid = None if keep is None else np.clip(1.0 - keep, 0.0, 1.0).astype(np.float32)
    face_cx = float((pts[234][0] + pts[454][0]) / 2.0)
    eye_y = float((pts[33][1] + pts[263][1]) / 2.0)
    chin_y = float(pts[152][1])
    top_y = float(pts[10][1])
    jaw_y = chin_y * 0.86 + top_y * 0.14

    def band(y, y0, y1, soft):
        return softstep((y - y0) / max(1e-3, soft)) * softstep((y1 - y) / max(1e-3, soft))

    if slim > 0:
        wx = np.maximum(0.0, 1.0 - (np.abs(mapx - face_cx) / (0.62 * fw)) ** 2)
        # 压缩带覆盖颧骨到下巴：起点在眉线，终点在下巴稍下，中段权重满值
        wy = band(mapy, float(pts[105][1]) - 0.02 * fw, chin_y + 0.02 * fw, 0.14 * fw)
        k = 0.095 * slim
        w = wx * wy
        if rigid is not None:
            w = w * rigid                          # 眼睛/嘴唇/眉毛不跟着横向压缩
        # 反向映射：取样点向外扩 → 像素内容向内收（脸变窄）
        mapx = face_cx + (mapx - face_cx) * (1.0 + k * w)

    if nose > 0:
        nose_cx = float((pts[129][0] + pts[358][0]) / 2.0)
        nose_y = float((pts[129][1] + pts[358][1]) / 2.0)
        nw = max(8.0, float(abs(pts[358][0] - pts[129][0])))
        r = nw * 1.15
        # 纵向范围收在鼻翼上下，避免把上唇/嘴一起挤窄
        wy = band(mapy, nose_y - r, nose_y + r * 0.45, r * 0.22)
        wx = np.maximum(0.0, 1.0 - (np.abs(mapx - nose_cx) / r) ** 2)
        w = wx * wy
        if rigid is not None:
            w = w * rigid
        mapx = nose_cx + (mapx - nose_cx) * (1.0 + 0.15 * nose * w)

    if eyesize > 0:
        for a, b, c_, d in ((33, 133, 159, 145), (263, 362, 386, 374)):
            ecx = float((pts[a][0] + pts[b][0]) / 2.0)
            ecy = float((pts[c_][1] + pts[d][1]) / 2.0)
            r = max(6.0, float(np.linalg.norm(pts[a] - pts[b])) * 1.25)
            dd = np.sqrt((mapx - ecx) ** 2 + (mapy - ecy) ** 2)
            t = np.maximum(0.0, 1.0 - (dd / r) ** 2)
            mag = 1.0 + 0.33 * eyesize * t * t
            mapx = ecx + (mapx - ecx) / mag
            mapy = ecy + (mapy - ecy) / mag

    if abs(chin) > 0:
        # 下巴：不是把「唇到下巴」整段往下推（会复制出双下颌），而是在唇部做锚点、
        # 对下半张脸做纵向拉伸：正值拉长、负值缩短，位移在唇部与下方都平滑归零。
        anchor_y = float(pts[0][1])                    # 上唇中线，拉伸不动点
        w_end = chin_y + 0.34 * fw                     # 影响区下沿（脖根前衰减完）
        up = softstep((mapy - anchor_y) / max(1e-3, 0.09 * fw))
        dn = softstep((w_end - mapy) / max(1e-3, 0.18 * fw))
        wgt = up * dn
        scale = 1.0 + 0.18 * chin * wgt                # >1 拉伸，<1 压缩
        mapy = anchor_y + (mapy - anchor_y) / np.maximum(0.55, scale)

    # 统一边界衰减：变形量在裁剪区边缘归零，贴回时不出现矩形接缝
    m = 0.06 * min(h, wd)
    edge = (softstep(base_x / m) * softstep((wd - base_x) / m)
            * softstep(base_y / m) * softstep((h - base_y) / m))
    mapx = base_x + (mapx - base_x) * edge
    mapy = base_y + (mapy - base_y) * edge

    mapx = mapx.astype(np.float32)
    mapy = mapy.astype(np.float32)
    return cv2.remap(crop, mapx, mapy, interpolation=cv2.INTER_LINEAR,
                     borderMode=cv2.BORDER_REFLECT101)


# ---------------------------------------------------------------- 增强层
def run_gfpgan(crop, strength):
    """GFPGAN 细节修复：只用它输出干净的低频（修斑点/肤色/噪点），
    原图的高频纹理按增益回注，避免 512 输入再放大把细节糊掉。"""
    try:
        session = get_session(GFPGAN_PATH)
    except Exception:
        return crop
    size = 512
    inp = cv2.resize(crop, (size, size), interpolation=cv2.INTER_LINEAR)
    x = inp.astype(np.float32) / 255.0
    x = (x - 0.5) / 0.5
    x = cv2.cvtColor(x, cv2.COLOR_BGR2RGB)
    x = np.transpose(x, (2, 0, 1))[None]
    in_name = session.get_inputs()[0].name
    out_name = session.get_outputs()[0].name
    o = session.run([out_name], {in_name: x})[0][0]
    o = np.transpose(o, (1, 2, 0))
    o = np.clip((o * 0.5 + 0.5) * 255.0, 0, 255).astype(np.uint8)
    o = cv2.cvtColor(o, cv2.COLOR_RGB2BGR)
    o = cv2.resize(o, (crop.shape[1], crop.shape[0]), interpolation=cv2.INTER_LINEAR)

    src = crop.astype(np.float32)
    ai = o.astype(np.float32)
    sigma = max(1.0, min(crop.shape[:2]) * 0.004)
    src_low = cv2.GaussianBlur(src, (0, 0), sigma)
    src_hi = src - src_low
    ai_low = cv2.GaussianBlur(ai, (0, 0), sigma)
    # AI 低频 + 原图高频（增益略大于 1，肉眼上才有"变清晰"的观感）
    gain = 1.0 + 0.45 * strength * np.clip(np.abs(src_hi).mean(2, keepdims=True) / 8.0, 0, 1)
    det = ai_low + src_hi * gain
    out = src + (det - src) * strength
    return np.clip(out, 0, 255).astype(np.uint8)


# ---------------------------------------------------------------- 单脸处理
def process_face(img, face, p, parser, mesher):
    cx, cy, fw, _ = face
    h, w = img.shape[:2]
    side = fw * 1.70
    crop_cy = cy - fw * 0.08
    x1, y1 = int(cx - side / 2), int(crop_cy - side / 2)
    x2, y2 = int(cx + side / 2), int(crop_cy + side / 2)
    x1, y1 = max(0, x1), max(0, y1)
    x2, y2 = min(w, x2), min(h, y2)
    if x2 - x1 < 24 or y2 - y1 < 24:
        return False

    crop = img[y1:y2, x1:x2].copy()
    ch, cw = crop.shape[:2]

    mask = parser.parse(crop)
    pts = mesher.predict(crop, (0, 0, cw, ch))

    skin = (mask == 1) | (mask == 10)          # 皮肤 + 鼻
    hsv_ref = cv2.cvtColor(crop, cv2.COLOR_BGR2HSV)
    # 唇：上/下唇直接取；口腔内部只取高饱和（红唇），排除牙齿白点
    lips = np.isin(mask, [12, 13]) | ((mask == 11) & (hsv_ref[:, :, 1] > 80))
    eyes = np.isin(mask, [4, 5])
    brows = np.isin(mask, [2, 3])
    protect = (eyes | brows | lips).astype(np.float32)

    skin_w = feather(skin.astype(np.float32), 0.010)
    # 眼/眉/唇外扩一圈再排除，保证五官边沿不被磨平
    prot = cv2.dilate(protect.astype(np.uint8), np.ones((5, 5), np.uint8)).astype(np.float32)
    skin_w = np.clip(skin_w - feather(prot, 0.012), 0, 1)
    # 液化用的刚性区（眼/眉/唇外扩并羽化，防止边缘出现折角）
    rigid_soft = feather(cv2.dilate(protect.astype(np.uint8), np.ones((11, 11), np.uint8)).astype(np.float32), 0.03)

    # 面颊：脸框中部两侧（用于红润）
    ys, xs = np.where(skin)
    if xs.size:
        fx1, fx2 = xs.min(), xs.max()
        fy1, fy2 = ys.min(), ys.max()
        cheek = np.zeros((ch, cw), np.float32)
        ax1, ax2 = int(fx1 + (fx2 - fx1) * 0.10), int(fx1 + (fx2 - fx1) * 0.34)
        ay1, ay2 = int(fy1 + (fy2 - fy1) * 0.50), int(fy1 + (fy2 - fy1) * 0.72)
        cheek[max(0, ay1):ay2, max(0, ax1):ax2] = 1.0
        bx1, bx2 = int(fx1 + (fx2 - fx1) * 0.66), int(fx1 + (fx2 - fx1) * 0.90)
        cheek[max(0, ay1):ay2, bx1:bx2] = 1.0
        cheek = cheek * (skin.astype(np.float32))
    else:
        cheek = np.zeros((ch, cw), np.float32)

    work = crop
    work = op_smooth(work, skin_w, p['smooth'] / 100.0)
    work = op_spot(work, skin_w, p['spot'] / 100.0, protect)
    work = op_white(work, skin_w, p['white'] / 100.0)
    work = op_dark_circle(work, mask, eyes.astype(np.float32), p['dark'] / 100.0)
    work = op_eye(work, eyes.astype(np.float32), p['eye'] / 100.0)
    work = op_lip(work, lips.astype(np.float32), p['lip'] / 100.0, int(p['lipcolor']))
    work = op_rosy(work, skin_w, cheek, p['rosy'] / 100.0)

    work = warp_all(work, pts, p, fw, keep=rigid_soft)

    if p['aifix'] > 0:
        work = run_gfpgan(work, p.get('aifix_strength', 55.0) / 100.0)

    # 羽化贴回
    paste = np.zeros((ch, cw), np.float32)
    k = max(4, int(min(ch, cw) * 0.02))
    paste[k:ch - k, k:cw - k] = 1.0
    paste = cv2.GaussianBlur(paste, (k * 2 + 1, k * 2 + 1), 0)
    mixed = blend(img[y1:y2, x1:x2], work, paste)
    img[y1:y2, x1:x2] = np.clip(mixed, 0, 255).astype(np.uint8)
    return True


# ---------------------------------------------------------------- 参数
DEFAULTS = {
    'smooth': 0.0, 'white': 0.0, 'spot': 0.0, 'dark': 0.0, 'eye': 0.0,
    'lip': 0.0, 'lipcolor': 2.0, 'rosy': 0.0, 'slim': 0.0, 'eyesize': 0.0,
    'nose': 0.0, 'chin': 0.0, 'aifix': 0.0, 'aifix_strength': 55.0,
}


def parse_params(argv):
    p = dict(DEFAULTS)
    for arg in argv:
        for kv in arg.split(','):
            if '=' not in kv:
                continue
            k, v = kv.split('=', 1)
            k = k.strip()
            if k in p:
                try:
                    p[k] = float(v)
                except ValueError:
                    pass
    return p


def process_image(input_path, output_path, p):
    img = cv2.imread(input_path)
    if img is None:
        return {"ok": False, "error": "cannot read: " + input_path}
    h, w = img.shape[:2]
    parser = FaceParser()
    mesher = FaceMesh()
    faces = detect_faces(img)
    n = 0
    for f in faces:
        if process_face(img, f, p, parser, mesher):
            n += 1
    q = 100 if p['aifix'] <= 0 else 96
    if output_path.lower().endswith(('.png', '.tif', '.tiff', '.bmp')):
        cv2.imwrite(output_path, img)          # 无损中转：不再二次压缩
    else:
        # 0x444 = 不做色度抽样，避免唇色/肤色被 4:2:0 糊掉
        cv2.imwrite(output_path, img, [cv2.IMWRITE_JPEG_QUALITY, q,
                                      cv2.IMWRITE_JPEG_SAMPLING_FACTOR, cv2.IMWRITE_JPEG_SAMPLING_FACTOR_444])
    return {"ok": True, "faces": n}


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(json.dumps({"ok": False, "error": "usage"}))
        sys.exit(1)
    inp, outp = sys.argv[1], sys.argv[2]
    p = parse_params(sys.argv[3:])
    t0 = time.time()
    try:
        r = process_image(inp, outp, p)
        r["ms"] = int((time.time() - t0) * 1000)
    except Exception as e:
        import traceback
        r = {"ok": False, "error": str(e), "tb": traceback.format_exc()[-800:]}
    print(json.dumps(r))
