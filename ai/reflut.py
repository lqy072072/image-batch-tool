"""参考图 -> 3D LUT (.cube) 生成器（纯算法，无模型）
思路：Reinhard 色彩迁移得到 3x3+偏移的全局映射，再叠一层逐通道 CDF 曲线还原参考图的影调。
用法: python reflut.py <参考图> <输出.cube>
"""
import json
import os
import sys

import cv2
import numpy as np

SIZE = 33


def load_rgb(path, max_edge=1024):
    img = cv2.imread(path, cv2.IMREAD_COLOR)
    if img is None:
        return None
    if max(img.shape[:2]) > max_edge:
        s = max_edge / max(img.shape[:2])
        img = cv2.resize(img, None, fx=s, fy=s, interpolation=cv2.INTER_AREA)
    return cv2.cvtColor(img, cv2.COLOR_BGR2RGB).astype(np.float32) / 255.0


def stats(pixels):
    m = pixels.mean(axis=0)
    s = pixels.std(axis=0)
    s[s < 1e-4] = 1e-4
    return m, s


def cdf_curve(ref_channel, grid=256):
    """逐通道 CDF 匹配：输入 0..1 -> 参考图对应分位"""
    hist, _ = np.histogram(ref_channel, bins=grid, range=(0, 1))
    cdf = np.cumsum(hist).astype(np.float32)
    cdf /= max(1.0, cdf[-1])
    lut = np.zeros(256, np.float32)
    for i in range(256):
        v = i / 255.0
        # 参考图 CDF 的反函数：给定输入亮度，取其分位位置的参考亮度
        idx = int(round(v * (grid - 1)))
        target_q = idx / (grid - 1)
        # 在 cdf 中找到 target_q 对应亮度
        pos = np.searchsorted(cdf, target_q)
        lut[i] = min(1.0, max(0.0, (pos + 0.5) / grid))
    # 与恒等曲线平滑混合，避免过猛
    ident = np.arange(256, dtype=np.float32) / 255.0
    return 0.55 * lut + 0.45 * ident


def make_cube(ref, out_path, title):
    m_r, s_r = stats(ref.reshape(-1, 3))
    # 中性源假设：灰色线性参考 (均值 0.45, 标准差 0.22)
    m_s = np.array([0.45, 0.45, 0.45], np.float32)
    s_s = np.array([0.22, 0.22, 0.22], np.float32)

    curves = [cdf_curve(ref[:, :, c].reshape(-1)) for c in range(3)]

    lines = ["TITLE \"%s\"" % title, "LUT_3D_SIZE %d" % SIZE, "DOMAIN_MIN 0 0 0", "DOMAIN_MAX 1 1 1", ""]
    axis = np.arange(SIZE, dtype=np.float32) / (SIZE - 1)
    for b in range(SIZE):
        for g in range(SIZE):
            for r in range(SIZE):
                v = np.array([axis[r], axis[g], axis[b]], np.float32)
                # 1) Reinhard 全局迁移
                v = (v - m_s) / s_s * s_r + m_r
                # 2) 逐通道影调曲线
                for c in range(3):
                    idx = int(np.clip(v[c], 0, 1) * 255)
                    v[c] = curves[c][idx]
                lines.append("%.6f %.6f %.6f" % (float(np.clip(v[0], 0, 1)),
                                                 float(np.clip(v[1], 0, 1)),
                                                 float(np.clip(v[2], 0, 1))))

    with open(out_path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")


def main():
    if len(sys.argv) < 3:
        print(json.dumps({"ok": False, "error": "args"}))
        return
    src, dst = sys.argv[1], sys.argv[2]
    ref = load_rgb(src)
    if ref is None:
        print(json.dumps({"ok": False, "error": "无法读取参考图"}))
        return
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    title = os.path.splitext(os.path.basename(dst))[0]
    make_cube(ref, dst, title)
    print(json.dumps({"ok": True, "file": dst, "size": SIZE, "bytes": os.path.getsize(dst)}))


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        print(json.dumps({"ok": False, "error": str(e)}))
