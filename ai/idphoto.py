"""证件照：人脸对齐裁切 + 换底色 + 尺寸预设 + 六寸排版
用法: python idphoto.py <in> <out> <sizeMode 0一寸/1二寸/2护照/3自定义> <bg 0白/1蓝/2红/3浅灰> <cw> <ch> <sheet 0/1> [<paperW> <paperH>]
"""
import json
import os
import sys

import cv2
import numpy as np

BASE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, BASE)
import beauty_pro as bp  # 复用已有的检测/解析实现

SIZES = [(295, 413), (413, 579), (390, 567)]
BGS = [(255, 255, 255), (219, 142, 60), (60, 60, 200), (235, 235, 235)]  # BGR: 白/蓝/红/浅灰


def person_mask(img, face=None):
    """人脸解析抠图：解析模型需要「人脸正、占比合适」的输入，
    所以先按人脸框做对齐裁切再解析，最后把 mask 逆变换回整图（非标准构图也不会漏背景）。"""
    h, w = img.shape[:2]
    parser = bp.FaceParser()

    if face is not None and face[2] > 20:
        cx, cy, fw = float(face[0]), float(face[1]), float(face[2])
        side = max(64.0, 2.2 * max(fw, w * 0.18))
        tw = int(round(side))
        th = int(round(side * 1.4))
        tw, th = max(64, min(tw, 1024)), max(64, min(th, 1434))
        sx, sy = tw / side, th / (side * 1.4)
        M = np.float32([[sx, 0, tw / 2.0 - cx * sx], [0, sy, th * 0.42 - cy * sy]])
        crop = cv2.warpAffine(img, M, (tw, th), flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE)
        labels = parser.parse(crop)
        m = (labels > 0).astype(np.float32)
        m = cv2.warpAffine(m, cv2.invertAffineTransform(M), (w, h), flags=cv2.INTER_LINEAR,
                           borderMode=cv2.BORDER_REPLICATE)
        mask = (m > 0.5).astype(np.uint8) * 255
    else:
        try:
            labels = parser.parse(img)
            mask = (labels > 0).astype(np.uint8) * 255
        except Exception:
            return None

    k = max(3, int(min(h, w) * 0.01))
    mask = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, np.ones((k, k), np.uint8))
    mask = cv2.morphologyEx(mask, cv2.MORPH_OPEN, np.ones((k, k), np.uint8))
    # 只保留最大连通域，去掉角落被误判的小块
    n, lb, stats, _ = cv2.connectedComponentsWithStats((mask > 0).astype(np.uint8), 8)
    if n > 1:
        lab = 1 + int(np.argmax(stats[1:, cv2.CC_STAT_AREA]))
        mask = np.where(lb == lab, 255, 0).astype(np.uint8)
    return mask


def crop_id(img, faces, W, H, x_shift=0.0):
    if not faces:
        # 无人脸：居中裁切到目标比例
        h, w = img.shape[:2]
        r = W / H
        if w / h > r:
            nw = int(h * r)
            img = img[:, (w - nw) // 2:(w - nw) // 2 + nw]
        else:
            nh = int(w / r)
            img = img[(h - nh) // 2:(h - nh) // 2 + nh, :]
        return cv2.resize(img, (W, H), interpolation=cv2.INTER_LANCZOS4), None, 1.0

    faces.sort(key=lambda d: -d[2])
    cx, cy, fw, _ = faces[0]
    # 头部（含发顶）高度约 1.55 倍人脸宽度；证件照里头部占画面高 ~60%，上方留白 10%
    head_h = 1.55 * fw
    k = (0.60 * H) / head_h
    crop_w = W / k
    crop_h = H / k
    head_top = cy - 1.28 * fw          # 比检测框再往上留出发顶（含发髻/蓬松头发）
    y0 = head_top - 0.10 * crop_h      # 头顶留白 ~10%
    x0 = cx - crop_w / 2.0 + x_shift   # x_shift 用于把「头部轮廓」而不是「脸框」摆到正中
    M = np.float32([[k, 0, -x0 * k], [0, k, -y0 * k]])
    out = cv2.warpAffine(img, M, (W, H), flags=cv2.INTER_LANCZOS4, borderMode=cv2.BORDER_REPLICATE)
    # 人脸在裁切结果里的新坐标，给抠图对齐用
    face_out = ((cx - x0) * k, (cy - y0) * k, fw * k)
    return out, face_out, k


def head_center_x(mask, W):
    """抠图 mask 里头部（上 45%）的水平中心：按每行左右边界取中位数，
    比「脸框中心」更贴近视觉上的头居中（含发髻、不对称鬓发）。"""
    ys, xs = np.where(mask > 127)
    if len(ys) == 0:
        return None
    ytop, ybot = int(ys.min()), int(ys.max())
    band = ytop + int((ybot - ytop) * 0.45)
    centers = []
    for y in range(ytop, band + 1):
        row = np.where(mask[y] > 127)[0]
        if len(row) >= 3:
            centers.append((float(row.min()) + float(row.max())) * 0.5)
    if not centers:
        return None
    return float(np.median(centers))


def crop_id_centered(img, faces, W, H):
    """先按脸框裁切，再按头部轮廓中心做 1~2 次水平校正，让头像在画面里居中。"""
    x_shift = 0.0
    portrait, face_out, k = crop_id(img, faces, W, H, x_shift)
    if not faces or k <= 0:
        return portrait, face_out
    mask = person_mask(portrait, face_out)
    if mask is None:
        return portrait, face_out
    for _ in range(2):
        c = head_center_x(mask, W)
        if c is None:
            break
        dx = c - W / 2.0
        if abs(dx) <= 1.0:
            break
        # 画面内位移换回原图位移（按裁切放大倍数）
        x_shift += dx / k
        portrait, face_out, k = crop_id(img, faces, W, H, x_shift)
        mask = person_mask(portrait, face_out)
        if mask is None:
            break
    return portrait, face_out


def replace_bg(img, color, face=None, mask=None):
    if mask is None:
        mask = person_mask(img, face)
    if mask is None:
        return img
    # 先向内收 1~2px 再羽化：边缘不留原背景暗边（发丝不会被背景色「冲淡」）
    er = max(1, int(min(img.shape[:2]) * 0.0015))
    mask2 = cv2.erode((mask > 127).astype(np.uint8), np.ones((er * 2 + 1, er * 2 + 1), np.uint8))
    alpha = cv2.GaussianBlur(mask2.astype(np.float32), (0, 0), max(1.0, min(img.shape[:2]) * 0.003))
    alpha = np.clip(alpha, 0, 1)
    # 颜色去污：边缘像素若接近底色，说明是半透明混合，按色差把 alpha 再压一点，
    # 发丝不会裹一圈亮边 / 原背景残留色
    bg = np.array(color, np.float32)
    d = np.linalg.norm(img.astype(np.float32) - bg[None, None, :], axis=2)
    a_color = np.clip(d / 55.0, 0, 1)
    edge = cv2.dilate(mask2.astype(np.uint8), np.ones((er * 4 + 1, er * 4 + 1), np.uint8)).astype(np.float32)
    edge = cv2.GaussianBlur(edge, (0, 0), max(1.0, er * 2.0))
    a_color = np.clip(a_color + (1.0 - edge), 0, 1)      # 只作用于边缘带
    alpha = np.minimum(alpha, np.maximum(a_color, alpha * 0.55))
    out = img.astype(np.float32) * alpha[:, :, None] + bg[None, None, :] * (1 - alpha[:, :, None])
    return np.clip(out, 0, 255).astype(np.uint8)


def estimate_shoulder_angle(mask):
    """肩线水平度：取抠图下半部分每列最低点拟合直线，返回倾角（度，正值右肩高）"""
    if mask is None:
        return None
    ys, xs = np.where(mask > 127)
    if len(ys) < 50:
        return None
    h, w = mask.shape[:2]
    y_cut = int(ys.min() + (ys.max() - ys.min()) * 0.72)   # 只看肩部及以下
    cols, bots = [], []
    for x in range(int(w * 0.12), int(w * 0.88)):
        col = np.where(mask[y_cut:, x] > 127)[0]
        if len(col) >= 2:
            cols.append(x)
            bots.append(y_cut + int(col.max()))
    if len(cols) < 20:
        return None
    cols = np.asarray(cols, np.float32)
    bots = np.asarray(bots, np.float32)
    k = np.polyfit(cols, bots, 1)[0]
    return float(np.degrees(np.arctan(k)))


def dpi_check(W, H, size_mode):
    """按证件照实物尺寸估算有效打印分辨率（300dpi 为冲印标准）"""
    mm = {0: (25.0, 35.0), 1: (35.0, 49.0), 2: (33.0, 48.0)}
    if size_mode in mm:
        mw, mh = mm[size_mode]
        return round(min(W / mw * 25.4, H / mh * 25.4), 1)
    return None


def make_sheet(img, W, H, SW=1800, SH=1200):
    """按指定相纸尺寸排版（默认六寸 1800x1200 @300dpi）"""
    gap = 12
    cols = (SW + gap) // (W + gap)
    rows = (SH + gap) // (H + gap)
    cols, rows = max(1, cols), max(1, rows)
    sheet = np.full((SH, SW, 3), 255, np.uint8)
    total_w = cols * W + (cols - 1) * gap
    total_h = rows * H + (rows - 1) * gap
    ox = (SW - total_w) // 2
    oy = (SH - total_h) // 2
    for r in range(rows):
        for c in range(cols):
            x = ox + c * (W + gap)
            y = oy + r * (H + gap)
            sheet[y:y + H, x:x + W] = img
    # 细裁切线
    for r in range(rows):
        for c in range(cols):
            x = ox + c * (W + gap)
            y = oy + r * (H + gap)
            cv2.rectangle(sheet, (x, y), (x + W - 1, y + H - 1), (200, 200, 200), 1)
    return sheet


def main():
    if len(sys.argv) < 8:
        print(json.dumps({"ok": False, "error": "args"}))
        return
    src, dst = sys.argv[1], sys.argv[2]
    size_mode = int(sys.argv[3])
    bg = int(sys.argv[4])
    cw, ch = int(sys.argv[5]), int(sys.argv[6])
    sheet = int(sys.argv[7])
    SW = int(sys.argv[8]) if len(sys.argv) > 8 else 1800
    SH = int(sys.argv[9]) if len(sys.argv) > 9 else 1200

    img = cv2.imread(src, cv2.IMREAD_COLOR)
    if img is None:
        print(json.dumps({"ok": False, "error": "无法读取图片"}))
        return

    W, H = SIZES[size_mode] if size_mode < 3 else (max(64, cw), max(64, ch))
    bgc = BGS[bg] if 0 <= bg < len(BGS) else BGS[0]

    # 大图先缩小，检测更快
    scale = 1.0
    if max(img.shape[:2]) > 2000:
        scale = 2000.0 / max(img.shape[:2])
        img = cv2.resize(img, None, fx=scale, fy=scale, interpolation=cv2.INTER_AREA)

    faces = bp.detect_faces(img)
    faces = [(c / scale, y / scale, f / scale, s) for (c, y, f, s) in faces] if scale != 1.0 else faces
    if scale != 1.0:
        # 裁切用原图坐标，重新读原图
        img = cv2.imread(src, cv2.IMREAD_COLOR)

    portrait, face_out = crop_id_centered(img.copy(), faces, W, H)
    p_mask = person_mask(portrait, face_out)
    portrait = replace_bg(portrait, bgc, face_out, p_mask)
    shoulder = estimate_shoulder_angle(p_mask)
    dpi = dpi_check(W, H, size_mode)

    if sheet:
        out = make_sheet(portrait, W, H, SW, SH)
    else:
        out = portrait

    # 输出带 300dpi 元数据，冲印店按 300dpi 打印不会再缩成 72dpi
    wrote = False
    try:
        from PIL import Image
        im = Image.fromarray(out[:, :, ::-1])
        if dst.lower().endswith((".jpg", ".jpeg")):
            im.save(dst, quality=96, subsampling=0, dpi=(300, 300))
        else:
            im.save(dst, dpi=(300, 300))
        wrote = True
    except Exception:
        wrote = False
    if not wrote:
        if dst.lower().endswith(".png"):
            cv2.imencode(".png", out)[1].tofile(dst)
        else:
            cv2.imencode(".jpg", out, [int(cv2.IMWRITE_JPEG_QUALITY), 96])[1].tofile(dst)
    print(json.dumps({"ok": True, "faces": len(faces), "w": out.shape[1], "h": out.shape[0],
                      "sheet": bool(sheet),
                      "shoulder": None if shoulder is None else round(shoulder, 1),
                      "dpi": dpi}))


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        print(json.dumps({"ok": False, "error": str(e)}))
