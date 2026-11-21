"""Unicode-safe OpenCV image IO for Windows paths containing Chinese characters."""
import os
import cv2
import numpy as np


def imread_u(path, flags=cv2.IMREAD_COLOR):
    """Read an image even when the path contains non-ASCII characters."""
    try:
        data = np.fromfile(path, dtype=np.uint8)
        if data.size:
            img = cv2.imdecode(data, flags)
            if img is not None:
                return img
    except Exception:
        pass
    return cv2.imread(path, flags)


def imwrite_u(path, img, params=None):
    """Write an image even when the path contains non-ASCII characters."""
    ext = os.path.splitext(path)[1] or ".png"
    try:
        ok, buf = cv2.imencode(ext, img, params or [])
        if not ok:
            return False
        with open(path, "wb") as f:
            f.write(buf.tobytes())
        return True
    except Exception:
        return cv2.imwrite(path, img, params or [])
