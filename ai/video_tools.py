# -*- coding: utf-8 -*-
"""v2.19 视频抽帧与简易裁剪（OpenCV）"""
import cv2
import json
import os
import sys


def out(obj):
    print(json.dumps(obj, ensure_ascii=False))
    sys.stdout.flush()


def main():
    if len(sys.argv) < 7:
        out({"ok": False, "error": "参数不足"})
        return 2
    mode, path, outdir, a, b, stem = sys.argv[1:7]
    if not os.path.isfile(path):
        out({"ok": False, "error": "视频不存在"})
        return 2
    os.makedirs(outdir, exist_ok=True)
    cap = cv2.VideoCapture(path)
    if not cap.isOpened():
        out({"ok": False, "error": "无法打开视频"})
        return 3
    try:
        fps = cap.get(cv2.CAP_PROP_FPS) or 25.0
        total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT) or 0)
        w = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH) or 0)
        h = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT) or 0)
        try:
            t = float(a)
        except Exception:
            t = 1.0
        if mode == "frame":
            cap.set(cv2.CAP_PROP_POS_MSEC, max(0.0, t) * 1000.0)
            ok, frame = cap.read()
            if not ok or frame is None:
                out({"ok": False, "error": "该时间点无法读取画面"})
                return 4
            outpath = os.path.join(outdir, "%s_frame_%.2fs.jpg" % (stem, max(0.0, t)))
            cv2.imwrite(outpath, frame, [int(cv2.IMWRITE_JPEG_QUALITY), 95])
            out({"ok": True, "path": outpath, "width": frame.shape[1], "height": frame.shape[0]})
            return 0
        if mode == "trim":
            try:
                start = max(0.0, float(a))
                end = max(start + 0.05, float(b))
            except Exception:
                start, end = 0.0, 10.0
            cap.set(cv2.CAP_PROP_POS_MSEC, start * 1000.0)
            outpath = os.path.join(outdir, "%s_trim_%.1f-%.1fs.mp4" % (stem, start, end))
            writer = cv2.VideoWriter(outpath, cv2.VideoWriter_fourcc(*"mp4v"), fps, (w, h))
            if not writer.isOpened():
                out({"ok": False, "error": "无法创建输出视频"})
                return 5
            written = 0
            while True:
                pos = cap.get(cv2.CAP_PROP_POS_MSEC) / 1000.0
                if pos >= end:
                    break
                ok, frame = cap.read()
                if not ok or frame is None:
                    break
                writer.write(frame)
                written += 1
            writer.release()
            if written <= 0:
                try:
                    os.remove(outpath)
                except Exception:
                    pass
                out({"ok": False, "error": "区间内没有可写帧"})
                return 6
            out({"ok": True, "path": outpath, "frames": written, "fps": fps, "total": total})
            return 0
        out({"ok": False, "error": "未知模式"})
        return 7
    finally:
        cap.release()


if __name__ == "__main__":
    sys.exit(main())
