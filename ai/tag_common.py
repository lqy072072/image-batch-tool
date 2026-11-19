"""多标签索引：把图片上能识别到的特征拆成可搜索的独立标签。

标签来源：
1. YOLOX 检测到的物体（含中文/英文别名）；
2. Chinese-CLIP 零样本场景/属性标签（同一张图可命中多个）；
3. PP-OCR 文字里的关键词。

一张图允许保存多个标签，搜索结果按“OCR → 标签/物体 → 语义向量”排序。
"""

import json
import re

from yolo_common import aliases_for

# label 是展示/搜索用的稳定标签，prompt 是送给 Chinese-CLIP 文本编码器的自然语言提示。
# 标签集会覆盖 iOS 相册里常见的开放搜索词；同一图片可以同时命中多个标签。
TAG_ITEMS = [
    # 人物与关系
    ("人", "一张有人的照片"),
    ("人物", "一张人物照片"),
    ("人脸", "一张人脸特写"),
    ("自拍", "一张自拍照片"),
    ("合影", "一张多人合影"),
    ("全家福", "一张全家福照片"),
    ("宝宝", "一张宝宝的照片"),
    ("儿童", "一张儿童的照片"),
    ("老人", "一张老人的照片"),
    ("婚礼", "一张婚礼现场照片"),
    ("证件照", "一张证件照"),
    ("街拍", "一张街头人像照片"),
    # 动物
    ("猫", "一张猫的照片"),
    ("狗", "一张狗的照片"),
    ("鸟", "一张鸟的照片"),
    ("鱼", "一张鱼的照片"),
    ("马", "一张马的照片"),
    ("羊", "一张羊的照片"),
    ("牛", "一张牛的照片"),
    ("宠物", "一张宠物的照片"),
    ("野生动物", "一张野生动物的照片"),
    # 自然与户外
    ("海边", "一张海边的照片"),
    ("沙滩", "一张沙滩的照片"),
    ("日落", "一张日落的照片"),
    ("日出", "一张日出的照片"),
    ("天空", "一张天空的照片"),
    ("云", "一张有云的照片"),
    ("山", "一张山的照片"),
    ("雪山", "一张雪山的照片"),
    ("湖", "一张湖的照片"),
    ("河流", "一张河流的照片"),
    ("森林", "一张森林的照片"),
    ("草地", "一张草地的照片"),
    ("花", "一张花的照片"),
    ("树", "一张树的照片"),
    ("雪", "一张下雪的照片"),
    ("雨天", "一张雨天的照片"),
    ("夜景", "一张夜景照片"),
    ("星空", "一张星空的照片"),
    ("城市", "一张城市照片"),
    ("建筑", "一张建筑照片"),
    ("街道", "一张街道照片"),
    ("桥", "一张桥的照片"),
    ("公园", "一张公园照片"),
    ("田野", "一张田野照片"),
    ("瀑布", "一张瀑布照片"),
    # 室内与生活
    ("室内", "一张室内照片"),
    ("窗外", "一张窗外的照片"),
    ("咖啡馆", "一张咖啡馆的照片"),
    ("餐厅", "一张餐厅的照片"),
    ("厨房", "一张厨房的照片"),
    ("客厅", "一张客厅的照片"),
    ("卧室", "一张卧室的照片"),
    ("办公室", "一张办公室的照片"),
    ("商店", "一张商店的照片"),
    ("食物", "一张食物的照片"),
    ("蛋糕", "一张蛋糕的照片"),
    ("咖啡", "一张咖啡的照片"),
    ("水果", "一张水果的照片"),
    ("早餐", "一张早餐的照片"),
    ("晚餐", "一张晚餐的照片"),
    ("饮料", "一张饮料的照片"),
    ("酒", "一张酒的照片"),
    ("书", "一张书的照片"),
    ("电脑", "一张电脑的照片"),
    ("手机", "一张手机的照片"),
    ("屏幕", "一张屏幕的照片"),
    ("文档", "一张文档的照片"),
    ("发票", "一张发票的照片"),
    ("合同", "一张合同的照片"),
    ("截图", "一张屏幕截图"),
    ("二维码", "一张二维码图片"),
    ("收据", "一张收据的照片"),
    ("名片", "一张名片的照片"),
    # 交通与地点
    ("汽车", "一张汽车的照片"),
    ("自行车", "一张自行车的照片"),
    ("飞机", "一张飞机的照片"),
    ("火车", "一张火车的照片"),
    ("船", "一张船的照片"),
    ("行李箱", "一张行李箱的照片"),
    ("花店", "一张花店的照片"),
    ("超市", "一张超市的照片"),
    ("医院", "一张医院的照片"),
    ("学校", "一张学校的照片"),
    ("地铁", "一张地铁的照片"),
    ("机场", "一张机场的照片"),
    # 画面风格与拍摄方式
    ("黑白", "一张黑白照片"),
    ("复古", "一张复古风格照片"),
    ("胶片", "一张胶片风格照片"),
    ("微距", "一张微距照片"),
    ("人像特写", "一张人像特写照片"),
    ("风景照", "一张风景照片"),
    ("逆光", "一张逆光照片"),
    ("低光", "一张低光照片"),
    ("长曝光", "一张长曝光照片"),
    ("广角", "一张广角照片"),
    # 事件与主题
    ("生日", "一张生日聚会照片"),
    ("旅行", "一张旅行照片"),
    ("聚会", "一张朋友聚会照片"),
    ("节日", "一张节日照片"),
    ("圣诞", "一张圣诞节照片"),
    ("运动", "一张运动照片"),
    ("演唱会", "一张演唱会照片"),
    ("展览", "一张展览照片"),
    ("毕业", "一张毕业照片"),
    ("宠物合影", "一张和宠物的合影"),
]

TAG_PROMPTS = [p for _, p in TAG_ITEMS]
TAG_LABELS = [t for t, _ in TAG_ITEMS]
TAG_LABEL_SET = set(TAG_LABELS)


def _norm(v):
    return re.sub(r"\s+", "", str(v or "")).strip().lower()


def _add(tags, seen, label, kind, score, src=None):
    label = str(label or "").strip()
    if not label:
        return
    key = (str(src or kind) + "|" + _norm(label))
    if not key or key in seen:
        return
    seen.add(key)
    item = {"t": label, "k": kind, "s": round(float(score), 4)}
    if src:
        item["src"] = str(src)
    tags.append(item)


def _parse_objects(objects_json):
    if not objects_json:
        return []
    try:
        data = json.loads(objects_json)
        if isinstance(data, list):
            return [d for d in data if isinstance(d, dict) and d.get("l")]
    except Exception:
        return []
    return []


def object_tags(detections=None, objects_json=None, obj_text="", max_tags=30):
    """把 YOLO 检测结果拆成多个独立标签，并补充中文/英文别名。"""
    tags = []
    seen = set()
    dets = detections if isinstance(detections, list) and detections else _parse_objects(objects_json)
    for d in dets:
        if not isinstance(d, dict):
            continue
        label = d.get("l") or ""
        if not label:
            continue
        score = d.get("c") or 0.0
        # 主标签使用检测框置信度；别名保留但置信度相同，方便中英文/同义词都能搜到。
        _add(tags, seen, label, "object", score, "yolo")
        for alias in aliases_for(label).split():
            _add(tags, seen, alias, "object", score, "yolo")
        if len(tags) >= max_tags:
            break
    # 旧索引只有 obj 文本、没有 objects JSON 时，按空格拆出已有标签。
    if not tags and obj_text:
        for token in str(obj_text).split():
            _add(tags, seen, token, "object", 0.5, "yolo")
            if len(tags) >= max_tags:
                break
    return tags


def ocr_tags(ocr_text="", max_tags=6):
    """从 OCR 文本提取短关键词作为标签；完整 OCR 仍然保存在 ocr 列里。"""
    text = str(ocr_text or "").strip()
    if not text:
        return []
    parts = re.split(r"[\s,，。；;：:/\\|·、\-—_=+()（）\[\]【】<>《》\"'“”‘’]+", text)
    tags = []
    seen = set()
    for p in parts:
        p = p.strip()
        if len(p) < 2 or len(p) > 18:
            continue
        if not re.search(r"[\u4e00-\u9fffA-Za-z0-9]", p):
            continue
        _add(tags, seen, p, "ocr", 1.0, "ocr")
        if len(tags) >= max_tags:
            break
    return tags


def scene_tags(image_emb, label_emb, min_score=0.24, top_k=6):
    """用 Chinese-CLIP 图文相似度给同一张图打多个场景/属性标签。"""
    if image_emb is None or label_emb is None:
        return []
    try:
        import numpy as np
        vec = np.asarray(image_emb, dtype=np.float32)
        if vec.ndim != 1:
            vec = vec.reshape(-1)
        mat = np.asarray(label_emb, dtype=np.float32)
        if mat.ndim != 2 or mat.shape[0] == 0 or mat.shape[1] != vec.size:
            return []
        scores = mat @ vec
        order = np.argsort(-scores)
        tags = []
        for idx in order[:max(1, int(top_k))]:
            score = float(scores[int(idx)])
            if score < float(min_score):
                break
            tags.append({"t": TAG_LABELS[int(idx)], "k": "scene", "src": "clip", "s": round(score, 4)})
        return tags
    except Exception:
        return []


def tags_have_sources(raw):
    """判断 tags 列是否已经是带来源的新格式；空列表视为已处理。"""
    if not raw:
        return False
    try:
        data = json.loads(raw)
    except Exception:
        return False
    if not isinstance(data, list):
        return False
    if not data:
        return True
    for item in data:
        if not isinstance(item, dict) or not item.get("t"):
            continue
        if not item.get("src"):
            return False
    return True


def build_tags(image_emb=None, label_emb=None, detections=None, objects_json=None,
               obj_text="", ocr_text="", max_object_tags=30, max_scene_tags=6,
               max_ocr_tags=6, min_scene_score=0.24):
    """合成一张图的多标签列表，并返回 (tags, tag_text)。

    三套模型的标签分别保留来源，允许同一个词同时以 YOLO/CLIP/OCR 标签出现，
    详情页按来源着色；搜索用的 tag_text 仍保持去重后的扁平文本。
    """
    tags = []
    seen = set()
    for item in object_tags(detections, objects_json, obj_text, max_object_tags):
        _add(tags, seen, item.get("t"), "object", item.get("s") or 0.0, "yolo")
    for item in scene_tags(image_emb, label_emb, min_scene_score, max_scene_tags):
        _add(tags, seen, item.get("t"), "scene", item.get("s") or 0.0, "clip")
    for item in ocr_tags(ocr_text, max_ocr_tags):
        _add(tags, seen, item.get("t"), "ocr", item.get("s") or 1.0, "ocr")
    # tag_text 用于 SQLite LIKE 检索；跨来源同名标签只保留一次，不改变搜索口径。
    labels = []
    label_seen = set()
    for t in tags:
        key = _norm(t.get("t"))
        if key and key not in label_seen:
            label_seen.add(key)
            labels.append(str(t.get("t") or "").strip().lower())
    text = " ".join(labels)
    return tags, text[:1200]
