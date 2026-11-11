"""AI全流程调色分析：接收原图，直接输出所有调色参数。
C#只负责应用，不做任何决策。
用法: python ai_process.py <input_image> <output_json> [lut_is_log] [lut_black] [lut_white]
"""
import sys
import json
import math
import numpy as np
from PIL import Image
import onnxruntime as rt

MODEL_PATH = r"ai\models\model.onnx"
MEAN = np.array([0.485, 0.456, 0.406], dtype=np.float32).reshape(3, 1, 1)
STD = np.array([0.229, 0.224, 0.225], dtype=np.float32).reshape(3, 1, 1)

def analyze(img_path, lut_is_log=False, lut_black=0.0, lut_white=1.0):
    img = Image.open(img_path).convert("RGB")
    W, H = img.size
    small = img.resize((224, 224), Image.BILINEAR)
    arr = np.array(small, dtype=np.float32) / 255.0  # HWC, 0-1

    # ===== 1. AI神经网络场景识别 =====
    chw = arr.transpose(2, 0, 1)
    chw = (chw - MEAN) / STD
    batch = chw[np.newaxis, ...]

    sess = rt.InferenceSession(MODEL_PATH, providers=["CPUExecutionProvider"])
    input_name = sess.get_inputs()[0].name
    probs = sess.run(None, {input_name: batch})[0][0]

    # 场景得分
    portrait_classes = set(range(0, 20)) | set(range(832, 846)) | set(range(281, 287)) | set(range(779, 791))
    landscape_classes = set(range(948, 960)) | set(range(970, 980)) | set(range(30, 40))
    food_classes = set(range(920, 940))
    arch_classes = set(range(480, 510))

    portrait_score = sum(float(probs[c]) for c in portrait_classes if c < 1000)
    landscape_score = sum(float(probs[c]) for c in landscape_classes if c < 1000)
    food_score = sum(float(probs[c]) for c in food_classes if c < 1000)
    arch_score = sum(float(probs[c]) for c in arch_classes if c < 1000)

    scores = {"portrait": portrait_score, "landscape": landscape_score,
              "food": food_score, "architecture": arch_score}
    scene = max(scores, key=scores.get)
    if scores[scene] < 0.1:
        scene = "generic"

    # ===== 2. AI分析图片颜色统计 =====
    full = np.array(img, dtype=np.float32) / 255.0  # HWC 0-1
    r, g, b = full[:,:,0], full[:,:,1], full[:,:,2]
    lum = 0.299 * r + 0.587 * g + 0.114 * b

    # 全局统计
    mean_lum = float(np.mean(lum))
    median_lum = float(np.median(lum))
    std_lum = float(np.std(lum))

    # 色偏检测
    mean_r = float(np.mean(r))
    mean_g = float(np.mean(g))
    mean_b = float(np.mean(b))

    # 高光/阴影分布
    highlight_mask = lum > 0.8
    shadow_mask = lum < 0.2
    highlight_lum = float(np.mean(lum[highlight_mask])) if highlight_mask.any() else 0.8
    shadow_lum = float(np.mean(lum[shadow_mask])) if shadow_mask.any() else 0.2

    # 动态范围
    p5 = float(np.percentile(lum, 5))
    p95 = float(np.percentile(lum, 95))
    dynamic_range = p95 - p5

    # ===== 3. 白平衡（基于全局色偏）=====
    gray = (mean_r + mean_g + mean_b) / 3.0
    wbR = gray / max(mean_r, 0.05)
    wbB = gray / max(mean_b, 0.05)
    wbG = gray / max(mean_g, 0.05)
    wbR = max(0.85, min(1.2, wbR))
    wbG = max(0.9, min(1.1, wbG))
    wbB = max(0.85, min(1.2, wbB))

    # ===== 4. 根据LUT类型计算预校准参数 =====
    # LUT是Log型：需要把sRGB映射到LUT期望的Log范围
    # LUT期望输入范围是 [lut_black, lut_white]
    # 我们需要把图片的中灰映射到这个范围的中值
    lut_range = max(lut_white - lut_black, 0.01)
    lut_mid = (lut_black + lut_white) / 2.0

    if lut_is_log:
        # Log型LUT：预曝光把原图中灰推到LUT期望的中灰位置
        # 原图中灰median_lum → lut_mid
        pre_exposure = lut_mid / max(median_lum, 0.05)
        pre_exposure = max(0.5, min(2.0, pre_exposure))
        # sRGB→Log转换曲线：AI根据图片动态范围决定压缩方式
        # 动态范围大→用对数压缩保留高光；小→线性映射
        use_log_convert = True
        log_gamma = 1.0
        if dynamic_range > 0.7:
            log_gamma = 0.6  # 宽动态范围，用对数压缩
        elif dynamic_range < 0.4:
            log_gamma = 1.3  # 窄动态范围，轻微拉伸
        else:
            log_gamma = 0.9
        # 饱和度：Log型LUT本身色彩饱满，AI不额外加饱和
        saturation = 1.0
        contrast = 1.0
    else:
        # 创意型LUT：不做Log转换，只做轻微白平衡，不调曝光
        pre_exposure = 1.0
        use_log_convert = False
        log_gamma = 1.0
        # 创意LUT：AI分析色彩丰富度决定是否补饱和
        max_ch = np.max(full, axis=2)
        min_ch = np.min(full, axis=2)
        colorfulness = np.where(max_ch > 0, (max_ch - min_ch) / np.maximum(max_ch, 0.01), 0)
        mean_colorfulness = float(np.mean(colorfulness))
        if mean_colorfulness < 0.15:
            saturation = 1.12
        elif mean_colorfulness < 0.25:
            saturation = 1.05
        elif mean_colorfulness > 0.5:
            saturation = 0.95
        else:
            saturation = 1.0
        if dynamic_range < 0.4:
            contrast = 1.1
        elif dynamic_range > 0.85:
            contrast = 0.95
        else:
            contrast = 1.02

    # 通用曝光（给独立AI优化pass用，不是预LUT用）
    exposure = 0.5 / max(median_lum, 0.05)
    exposure = max(0.6, min(1.6, exposure))

    # 高光/阴影调整
    highlights = 1.0
    shadows = 1.0
    if highlight_lum > 0.9:
        highlights = 0.92
    if shadow_lum < 0.1:
        shadows = 1.08

    # 根据场景微调
    if scene == "portrait":
        wbR *= 1.03
        wbB *= 0.97
        saturation *= 0.95
        contrast *= 0.98
    elif scene == "landscape":
        contrast *= 1.05
        saturation *= 1.05
    elif scene == "food":
        wbR *= 1.05
        wbB *= 0.95
        saturation *= 1.08
    elif scene == "architecture":
        contrast *= 1.08
        saturation *= 0.95

    return {
        "scene": scene,
        "scene_score": {k: round(v, 4) for k, v in scores.items()},
        "wbR": round(wbR, 4),
        "wbG": round(wbG, 4),
        "wbB": round(wbB, 4),
        "exposure": round(exposure, 4),
        "preExposure": round(pre_exposure, 4),
        "useLogConvert": use_log_convert,
        "logGamma": round(log_gamma, 4),
        "lutBlack": round(lut_black, 4),
        "lutWhite": round(lut_white, 4),
        "contrast": round(contrast, 4),
        "saturation": round(saturation, 4),
        "highlights": round(highlights, 4),
        "shadows": round(shadows, 4),
        "temperature": 0.0,
        "tint": 0.0,
        "stats": {
            "mean_lum": round(mean_lum, 4),
            "median_lum": round(median_lum, 4),
            "dynamic_range": round(dynamic_range, 4)
        }
    }

if __name__ == "__main__":
    img_path = sys.argv[1]
    out_path = sys.argv[2]
    lut_is_log = len(sys.argv) > 3 and sys.argv[3].lower() in ("1", "true", "yes")
    lut_black = float(sys.argv[4]) if len(sys.argv) > 4 else 0.0
    lut_white = float(sys.argv[5]) if len(sys.argv) > 5 else 1.0
    result = analyze(img_path, lut_is_log, lut_black, lut_white)
    with open(out_path, "w") as f:
        json.dump(result, f, indent=2)
    print(json.dumps(result, indent=2))
