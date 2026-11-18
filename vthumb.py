# -*- coding: utf-8 -*-
"""相册视频封面抽取器。
用法: python vthumb.py <jobs.json>
jobs.json = [[源视频绝对路径, 目标jpg绝对路径], ...]
对每个任务抓取一帧(默认约 1 秒 / 全片 10% 处)，缩放到最长边 480 存 jpg。
失败不抛异常，只是不产生目标文件。
"""
import json
import os
import sys

import cv2

MAX_SIDE = 480


def grab(src, dst):
    cap = cv2.VideoCapture(src)
    if not cap.isOpened():
        return False
    try:
        fps = cap.get(cv2.CAP_PROP_FPS) or 0
        frames = cap.get(cv2.CAP_PROP_FRAME_COUNT) or 0
        dur = (frames / fps) if (fps > 0 and frames > 0) else 0
        seek_ms = min(1000.0, (dur / 10.0) * 1000.0) if dur > 0 else 500.0
        cap.set(cv2.CAP_PROP_POS_MSEC, seek_ms)
        ok, frame = cap.read()
        if not ok or frame is None:
            cap.set(cv2.CAP_PROP_POS_FRAMES, 0)
            ok, frame = cap.read()
        if not ok or frame is None:
            cap.set(cv2.CAP_PROP_POS_MSEC, max(200.0, dur * 300.0))
            ok, frame = cap.read()
        if not ok or frame is None:
            return False
        h, w = frame.shape[:2]
        k = min(1.0, float(MAX_SIDE) / max(1, max(h, w)))
        if k < 1.0:
            frame = cv2.resize(frame, (max(1, int(w * k)), max(1, int(h * k))),
                               interpolation=cv2.INTER_AREA)
        d = os.path.dirname(dst)
        if d and not os.path.isdir(d):
            os.makedirs(d)
        tmp = dst + ".tmp.jpg"
        if not cv2.imwrite(tmp, frame, [int(cv2.IMWRITE_JPEG_QUALITY), 82]):
            return False
        os.replace(tmp, dst)
        return True
    finally:
        cap.release()


def main():
    if len(sys.argv) < 2:
        return 2
    with open(sys.argv[1], "r", encoding="utf-8") as f:
        jobs = json.load(f)
    ok = 0
    for src, dst in jobs:
        try:
            if os.path.isfile(dst):
                ok += 1
                continue
            if grab(src, dst):
                ok += 1
        except Exception:
            pass
    print("vthumb done %d/%d" % (ok, len(jobs)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
