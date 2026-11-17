"""色卡提取 / 配色方案导出
用法: python palette.py <图片> <色数 3-12>
输出 JSON: {"ok":true,"colors":[{"hex":"#aabbcc","rgb":[r,g,b],"ratio":0.12}],...}
"""
import json
import sys

import cv2
import numpy as np


def kmeans(pixels, k, iters=14):
    # 均匀抽取初始中心（不依赖 numpy.random，嵌入版 numpy 可能被裁剪）
    step = max(1, len(pixels) // k)
    centers = np.array([pixels[min(len(pixels) - 1, i * step)] for i in range(k)], np.float32)
    centers += np.linspace(-3, 3, k, dtype=np.float32)[:, None]
    for _ in range(iters):
        d = ((pixels[:, None, :] - centers[None, :, :]) ** 2).sum(2)
        idx = d.argmin(1)
        for j in range(k):
            sel = pixels[idx == j]
            if len(sel):
                centers[j] = sel.mean(0)
    d = ((pixels[:, None, :] - centers[None, :, :]) ** 2).sum(2)
    idx = d.argmin(1)
    counts = np.bincount(idx, minlength=k).astype(np.float32)
    return centers, counts / max(1, counts.sum())


def main():
    if len(sys.argv) < 3:
        print(json.dumps({"ok": False, "error": "args"}))
        return
    path = sys.argv[1]
    k = int(sys.argv[2])
    k = max(3, min(12, k))

    img = cv2.imread(path, cv2.IMREAD_COLOR)
    if img is None:
        print(json.dumps({"ok": False, "error": "无法读取图片"}))
        return

    if max(img.shape[:2]) > 480:
        s = 480.0 / max(img.shape[:2])
        img = cv2.resize(img, None, fx=s, fy=s, interpolation=cv2.INTER_AREA)

    rgb = cv2.cvtColor(img, cv2.COLOR_BGR2RGB).reshape(-1, 3).astype(np.float32)
    # 去掉极暗/极亮噪点，保留主体色
    lum = rgb.mean(1)
    keep = (lum > 8) & (lum < 250)
    rgb = rgb[keep]
    if len(rgb) < k * 10:
        print(json.dumps({"ok": False, "error": "有效像素太少"}))
        return
    if len(rgb) > 60000:
        rgb = rgb[:: max(1, len(rgb) // 60000)]

    centers, ratios = kmeans(rgb, k)
    order = np.argsort(-ratios)
    colors = []
    for i in order:
        c = np.clip(centers[i], 0, 255).astype(int)
        colors.append({
            "hex": "#%02x%02x%02x" % (c[0], c[1], c[2]),
            "rgb": [int(c[0]), int(c[1]), int(c[2])],
            "ratio": round(float(ratios[i]), 4),
        })

    print(json.dumps({"ok": True, "count": len(colors), "colors": colors}, ensure_ascii=True))


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        print(json.dumps({"ok": False, "error": str(e)}))
