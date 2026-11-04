"""共通部品: 使う計算機(CPU/GPU)の決定、画像の読み書き、骨格(YOLO-pose)のキーポイント定義と教師データの読み書き。

annotation.py(アノテーション)・train.py(学習)・validation.py(検証)で共通。

キーポイントは COCO の17点をそのまま使う。こうすると、標準の学習済みモデル
(yolo11n-pose.pt)から続けて学習(ファインチューニング)できる。
このプログラムで使うのは、頭(鼻・目・耳)、肩、肘、手首、腰。膝と足首は使わないので、
アノテーションでは付けず、「無し」(v=0)として保存する(学習では無視される)。

教師データのフォルダ:
    <dataset>/images/train/*.jpg    <dataset>/images/val/*.jpg
    <dataset>/labels/train/*.txt    <dataset>/labels/val/*.txt
    <dataset>/data.yaml
ラベル(1人1行): 0 cx cy w h  x1 y1 v1  x2 y2 v2 ... x17 y17 v17   (0〜1 に正規化)
    v: 0 = 無し(ラベル無し) / 1 = 隠れているが位置は分かる / 2 = 見えている
"""

from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np

KEYPOINT_NAMES = [
    "nose",
    "left_eye",
    "right_eye",
    "left_ear",
    "right_ear",
    "left_shoulder",
    "right_shoulder",
    "left_elbow",
    "right_elbow",
    "left_wrist",
    "right_wrist",
    "left_hip",
    "right_hip",
    "left_knee",
    "right_knee",
    "left_ankle",
    "right_ankle",
]
NUM_KEYPOINTS = len(KEYPOINT_NAMES)

NOSE, L_EYE, R_EYE, L_EAR, R_EAR = 0, 1, 2, 3, 4
L_SHOULDER, R_SHOULDER, L_ELBOW, R_ELBOW, L_WRIST, R_WRIST = 5, 6, 7, 8, 9, 10
L_HIP, R_HIP = 11, 12

# 左右を入れ替える対応(学習時の左右反転で使う)
FLIP_IDX = [0, 2, 1, 4, 3, 6, 5, 8, 7, 10, 9, 12, 11, 14, 13, 16, 15]

HEAD_POINTS = (NOSE, L_EYE, R_EYE, L_EAR, R_EAR)  # 頭の位置は、このうち見える点の平均
SHOULDERS = (L_SHOULDER, R_SHOULDER)
HIPS = (L_HIP, R_HIP)
WRISTS = (L_WRIST, R_WRIST)  # 両手の位置

# アノテーションで付ける点(目は付けない。鼻と耳があれば頭の位置は分かる)
ANNOTATED = [NOSE, L_EAR, R_EAR, L_SHOULDER, R_SHOULDER, L_ELBOW, R_ELBOW, L_WRIST, R_WRIST, L_HIP, R_HIP]

# 描画用の線
SKELETON = [
    (NOSE, L_EAR),
    (NOSE, R_EAR),
    (L_SHOULDER, R_SHOULDER),
    (L_SHOULDER, L_ELBOW),
    (L_ELBOW, L_WRIST),
    (R_SHOULDER, R_ELBOW),
    (R_ELBOW, R_WRIST),
    (L_SHOULDER, L_HIP),
    (R_SHOULDER, R_HIP),
    (L_HIP, R_HIP),
]

IMAGE_SUFFIXES = {".jpg", ".jpeg", ".png", ".bmp"}


# ------------------------------------------------------------------ 画像の読み書き


def read_image(path):
    """日本語などを含むパスでも読める imread。読めなければ None。"""
    try:
        data = np.fromfile(str(path), np.uint8)
    except OSError:
        return None
    if data.size == 0:
        return None
    return cv2.imdecode(data, cv2.IMREAD_COLOR)


def write_image(path, image, quality=95):
    """日本語などを含むパスでも書ける imwrite(cv2.imwrite は、そのようなパスに黙って失敗する)。"""
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    ok, buffer = cv2.imencode(path.suffix or ".jpg", image, [cv2.IMWRITE_JPEG_QUALITY, quality])
    if not ok:
        raise OSError(f"Could not encode the image: {path}")
    buffer.tofile(str(path))
    if not path.exists() or path.stat().st_size == 0:
        raise OSError(f"Could not write the image: {path}")


# ------------------------------------------------------------------ ラベル


def box_from_keypoints(keypoints, width, height, pad=0.25):
    """
    付けたキーポイント(v>0)を囲む枠 (x0, y0, x1, y1)。上半身だけを付けるので、それを囲む枠にする。
    pad: 枠の幅・高さに対する余白の割合。付けた点が無ければ None。
    """
    visible = keypoints[:, 2] > 0
    if not visible.any():
        return None
    xs, ys = keypoints[visible, 0], keypoints[visible, 1]
    x0, x1, y0, y1 = xs.min(), xs.max(), ys.min(), ys.max()
    pad_x = max(pad * (x1 - x0), 0.03 * width)
    pad_y = max(pad * (y1 - y0), 0.03 * height)
    return (
        float(max(0, x0 - pad_x)),
        float(max(0, y0 - pad_y)),
        float(min(width - 1, x1 + pad_x)),
        float(min(height - 1, y1 + pad_y)),
    )


def label_text(keypoints, width, height):
    """キーポイント (17, 3)(画素)から、ラベル1行を作る。付けた点が無ければ None。"""
    box = box_from_keypoints(keypoints, width, height)
    if box is None:
        return None
    x0, y0, x1, y1 = box
    values = [0, (x0 + x1) / 2 / width, (y0 + y1) / 2 / height, (x1 - x0) / width, (y1 - y0) / height]
    for x, y, v in keypoints:
        if v > 0:
            values += [min(max(x / width, 0.0), 1.0), min(max(y / height, 0.0), 1.0), int(v)]
        else:
            values += [0.0, 0.0, 0]
    return " ".join([str(values[0])] + [f"{v:.6f}" if isinstance(v, float) else str(v) for v in values[1:]])


def read_label(path, width, height):
    """
    ラベルファイルを読む。人ごとの dict のリスト:
        {"box": (x0, y0, x1, y1), "keypoints": (17, 3) の配列(画素, 3列目は v)}
    """
    people = []
    for line in Path(path).read_text(encoding="utf-8").splitlines():
        parts = line.split()
        if len(parts) != 5 + 3 * NUM_KEYPOINTS:
            continue
        values = np.array(parts[1:], np.float64)
        cx, cy, w, h = values[:4]
        kps = values[4:].reshape(NUM_KEYPOINTS, 3).copy()
        kps[:, 0] *= width
        kps[:, 1] *= height
        box = ((cx - w / 2) * width, (cy - h / 2) * height, (cx + w / 2) * width, (cy + h / 2) * height)
        people.append({"box": box, "keypoints": kps})
    return people


def write_label(path, keypoints, width, height):
    """ラベルを書く(人は1人)。付けた点が無ければ書かず False を返す。"""
    text = label_text(keypoints, width, height)
    if text is None:
        return False
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    Path(path).write_text(text + "\n", encoding="utf-8")
    return True


# ------------------------------------------------------------------ データセット


def write_data_yaml(root):
    """data.yaml を書く。パスは絶対パスにして、どこから学習を始めても読めるようにする。"""
    root = Path(root).resolve()
    lines = [
        f"path: {root.as_posix()}",
        "train: images/train",
        "val: images/val",
        f"kpt_shape: [{NUM_KEYPOINTS}, 3]",
        f"flip_idx: {FLIP_IDX}",
        "names:",
        "  0: person",
        "",
    ]
    (root / "data.yaml").write_text("\n".join(lines), encoding="utf-8")
    return root / "data.yaml"


def list_samples(root, split):
    """
    データセットの (画像パス, ラベルパス) の一覧。split は "train" / "val" / "all"。
    ラベルの無い画像は含めない。
    """
    root = Path(root)
    splits = ["train", "val"] if split == "all" else [split]
    samples = []
    for name in splits:
        for image in sorted((root / "images" / name).glob("*")):
            if image.suffix.lower() not in IMAGE_SUFFIXES:
                continue
            label = root / "labels" / name / (image.stem + ".txt")
            if label.exists():
                samples.append((image, label))
    return samples


# ------------------------------------------------------------------ CPU / GPU の切り替え


@dataclass
class Device:
    name: str  # ultralytics に渡す値: "cpu" / "0"(1番目の GPU)/ "mps"
    half: bool  # FP16(半精度)で推論するか(GPU のとき速くなる)
    label: str  # 表示用

    @property
    def is_gpu(self):
        return self.name != "cpu"


def resolve_device(device="auto", half=None):
    """
    使う計算機を決める。device: "auto"(GPU があれば GPU)/ "cpu" / "cuda" / "cuda:1" / "0" / "mps"。
    half: None なら CUDA のときだけ FP16。True / False で指定もできる(CPU・mps では常に FP32)。
    指定した GPU が使えないときは警告して CPU にする。
    """
    import torch

    request = str(device or "auto").strip().lower()
    cuda_ok = torch.cuda.is_available()
    mps_ok = bool(getattr(torch.backends, "mps", None) and torch.backends.mps.is_available())

    if request == "auto":
        request = "0" if cuda_ok else ("mps" if mps_ok else "cpu")
    elif request in ("cuda", "gpu"):
        request = "0"
    elif request.startswith("cuda:"):
        request = request.split(":", 1)[1]

    if request == "mps" and not mps_ok:
        print("Warning: mps is not available; using the CPU.")
        request = "cpu"
    elif request.replace(",", "").isdigit() and not cuda_ok:
        print("Warning: CUDA (GPU) is not available in this PyTorch; using the CPU. "
              "(Install a CUDA build of PyTorch to use the GPU.)")
        request = "cpu"

    use_half = request.replace(",", "").isdigit() if half is None else bool(half) and request != "cpu" and request != "mps"
    if request == "cpu":
        label = "CPU"
    elif request == "mps":
        label = "Apple GPU (mps)"
    else:
        label = f"GPU {request} ({torch.cuda.get_device_name(int(request.split(',')[0]))})"
    return Device(request, use_half, label + (", FP16" if use_half else ""))
