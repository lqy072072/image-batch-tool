import os
import sys
import unicodedata

import numpy as np
import cv2
from imgio import imread_u, imwrite_u

_MODEL_DIR = None
_ORT = None
_DML_READY = None


def set_model_dir(path):
    global _MODEL_DIR
    _MODEL_DIR = os.path.abspath(path)


def model_dir():
    return _MODEL_DIR


def _prepare_dml():
    global _DML_READY
    if _DML_READY is not None:
        return _DML_READY
    dml = os.path.join(_MODEL_DIR or "", "dml")
    if os.path.isdir(dml):
        if dml not in sys.path:
            sys.path.insert(0, dml)
        _DML_READY = True
    else:
        _DML_READY = False
    return _DML_READY


def get_ort():
    global _ORT
    if _ORT is not None:
        return _ORT
    _prepare_dml()
    import onnxruntime as ort
    _ORT = ort
    return ort


def create_session(model_path, gpu=True, threads=4):
    ort = get_ort()
    providers = ["CPUExecutionProvider"]
    if gpu:
        try:
            avail = ort.get_available_providers()
            if "DmlExecutionProvider" in avail:
                providers = ["DmlExecutionProvider", "CPUExecutionProvider"]
        except Exception:
            pass
    opts = ort.SessionOptions()
    # DirectML 下关闭内存复用，避免长跑索引时显存持续增长；CPU 下保留默认优化。
    if providers[0] == "DmlExecutionProvider":
        try:
            opts.enable_mem_pattern = False
            opts.enable_cpu_mem_arena = False
        except Exception:
            pass
    # CPU provider 用这里控制线程；DML provider 下仍让 ORT 的 CPU 前后处理受控，
    # 不支持该选项时会被 ORT 忽略。
    try:
        opts.intra_op_num_threads = max(1, int(threads))
    except Exception:
        pass
    try:
        return ort.InferenceSession(model_path, sess_options=opts, providers=providers), providers
    except Exception:
        # DirectML 不可用/不支持该模型时回退 CPU，搜索仍可用。
        if providers[0] != "CPUExecutionProvider":
            return ort.InferenceSession(model_path, sess_options=opts, providers=["CPUExecutionProvider"]), ["CPUExecutionProvider"]
        raise


class BertTokenizer:
    def __init__(self, vocab_path, max_len=52):
        with open(vocab_path, "r", encoding="utf-8") as f:
            self.vocab = [line.rstrip("\n") for line in f]
        self.id = {t: i for i, t in enumerate(self.vocab)}
        self.pad = self.id.get("[PAD]", 0)
        self.unk = self.id.get("[UNK]", 100)
        self.cls = self.id.get("[CLS]", 101)
        self.sep = self.id.get("[SEP]", 102)
        self.max_len = max_len
        self.puncts = set("!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~")
        self.never_split = {"[UNK]", "[SEP]", "[PAD]", "[CLS]", "[MASK]"}

    @staticmethod
    def _is_cjk(ch):
        return ("\u4e00" <= ch <= "\u9fff") or ("\u3400" <= ch <= "\u4dbf")

    def basic_tokens(self, text):
        text = unicodedata.normalize("NFD", text)
        text = "".join(c for c in text if unicodedata.category(c) != "Mn")
        text = text.lower()
        out = []
        for ch in text:
            if ch.isspace():
                out.append(" ")
            elif self._is_cjk(ch):
                out.append(" " + ch + " ")
            elif ch in self.puncts:
                out.append(" " + ch + " ")
            else:
                out.append(ch)
        text = "".join(out)
        return [t for t in text.strip().split() if t]

    def wordpiece(self, token):
        if token in self.never_split:
            return [self.id.get(token, self.unk)]
        if len(token) > 100:
            return [self.unk]
        if token in self.id:
            return [self.id[token]]
        chars = list(token)
        begin = 0
        sub = []
        while begin < len(chars):
            end = len(chars)
            cur = None
            while begin < end:
                piece = "".join(chars[begin:end])
                if begin > 0:
                    piece = "##" + piece
                if piece in self.id:
                    cur = piece
                    break
                end -= 1
            if cur is None:
                return [self.unk]
            sub.append(self.id[cur])
            begin = end
        return sub

    def encode(self, text):
        ids = [self.cls]
        for t in self.basic_tokens(text or ""):
            ids.extend(self.wordpiece(t))
            if len(ids) >= self.max_len - 1:
                break
        ids = ids[:self.max_len - 1] + [self.sep]
        ids += [self.pad] * (self.max_len - len(ids))
        return ids


def preprocess_image(path):
    # 相册缩略图优先；原图也支持。用 BGR->RGB + CLIP 标准归一化。
    data = np.fromfile(path, dtype=np.uint8)
    im = cv2.imdecode(data, cv2.IMREAD_COLOR)
    if im is None:
        raise RuntimeError("decode_failed")
    h, w = im.shape[:2]
    short = min(h, w)
    if short < 224:
        scale = 224.0 / max(1, short)
        im = cv2.resize(im, (max(224, int(round(w * scale))), max(224, int(round(h * scale)))), interpolation=cv2.INTER_CUBIC)
    elif short > 224:
        scale = 224.0 / short
        im = cv2.resize(im, (max(224, int(round(w * scale))), max(224, int(round(h * scale)))), interpolation=cv2.INTER_CUBIC)
    h, w = im.shape[:2]
    x0 = max(0, (w - 224) // 2)
    y0 = max(0, (h - 224) // 2)
    im = im[y0:y0 + 224, x0:x0 + 224]
    im = cv2.cvtColor(im, cv2.COLOR_BGR2RGB).astype(np.float32) / 255.0
    mean = np.array([0.48145466, 0.4578275, 0.40821073], dtype=np.float32)
    std = np.array([0.26862954, 0.26130258, 0.27577711], dtype=np.float32)
    im = (im - mean) / std
    im = im.transpose(2, 0, 1)
    return np.ascontiguousarray(im[None, ...])


def l2norm(x):
    x = np.asarray(x, dtype=np.float32)
    return x / (np.linalg.norm(x, axis=-1, keepdims=True) + 1e-12)
