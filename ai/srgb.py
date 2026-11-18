"""把内嵌 ICC 的图片统一转成 sRGB（输出给手机/网页不再偏色）
用法: python srgb.py <img>           就地重写
     python srgb.py <in> <out>      另存
只处理有 ICC 且不是 sRGB 的图；没有 ICC 视为 sRGB，原样返回。
"""
import io
import json
import sys

from PIL import Image, ImageCms


def is_srgb(profile):
    try:
        desc = ImageCms.getProfileDescription(profile) or ""
    except Exception:
        desc = ""
    d = desc.lower()
    return ("srgb" in d) or ("iec61966" in d) or ("sRGB" in desc)


def main():
    if len(sys.argv) < 2:
        print(json.dumps({"ok": False, "error": "args"}))
        return
    src = sys.argv[1]
    dst = sys.argv[2] if len(sys.argv) > 2 else src

    try:
        im = Image.open(src)
    except Exception as e:
        print(json.dumps({"ok": False, "error": "无法读取: %s" % e}))
        return

    icc = im.info.get("icc_profile")
    if not icc:
        if dst != src:
            im.save(dst, quality=96, subsampling=0)
        print(json.dumps({"ok": True, "converted": False, "reason": "无 ICC，按 sRGB 处理"}))
        return

    try:
        src_prof = ImageCms.ImageCmsProfile(io.BytesIO(icc))
    except Exception as e:
        print(json.dumps({"ok": True, "converted": False, "reason": "ICC 无法解析: %s" % e}))
        return

    if is_srgb(src_prof):
        if dst != src:
            im.save(dst, quality=96, subsampling=0)
        print(json.dumps({"ok": True, "converted": False, "reason": "已经是 sRGB"}))
        return

    try:
        srgb = ImageCms.createProfile("sRGB")
        out = ImageCms.profileToProfile(im.convert("RGB"), src_prof, srgb, outputMode="RGB")
    except Exception as e:
        print(json.dumps({"ok": False, "error": "转换失败: %s" % e}))
        return

    # 输出仍然嵌入 sRGB profile，避免下游再猜
    out.save(dst, quality=96, subsampling=0, icc_profile=ImageCms.ImageCmsProfile(srgb).tobytes() if hasattr(ImageCms.ImageCmsProfile(srgb), "tobytes") else icc)
    try:
        desc = ImageCms.getProfileDescription(src_prof)
    except Exception:
        desc = "unknown"
    print(json.dumps({"ok": True, "converted": True, "profile": desc}))


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        print(json.dumps({"ok": False, "error": str(e)}))
