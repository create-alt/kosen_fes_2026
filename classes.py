"""YOLO で扱うクラス(物体の種類)の定義と、検出結果の入れ物。

クラス番号は、教師データ(collect_dataset.py)・学習(train_yolo.py)・推論(yolo_pose.py)で
共通。data.yaml には、データに写っていないクラスも含めて、常にこの4つを書く。
"""

from dataclasses import dataclass
from typing import Optional

import numpy as np

BAG = "bag"
GLOVE = "glove"
HEAD = "head"
BODY = "body"

# (キー名, クラス番号, data.yaml の名前)。クラス番号は後から変えないこと(学習済みモデルと食い違う)
CLASSES = [
    (BAG, 0, "punching_bag"),
    (GLOVE, 1, "glove"),
    (HEAD, 2, "head"),
    (BODY, 3, "body"),
]
CLASS_ID = {key: number for key, number, _ in CLASSES}
YAML_NAME = {key: name for key, _, name in CLASSES}
ALL_KEYS = [key for key, _, _ in CLASSES]

# 学習済みモデル側の名前のゆれを吸収する
_ALIASES = {
    "punching_bag": BAG,
    "punchingbag": BAG,
    "bag": BAG,
    "sandbag": BAG,
    "glove": GLOVE,
    "boxing_glove": GLOVE,
    "gloves": GLOVE,
    "head": HEAD,
    "body": BODY,
    "torso": BODY,
}


def key_from_model_name(name):
    """YOLO モデルのクラス名から、BAG / GLOVE / HEAD / BODY のキーを返す(未知なら None)。"""
    return _ALIASES.get(str(name).strip().lower().replace(" ", "_").replace("-", "_"))


@dataclass
class ClassSettings:
    """データ収集で、クラスごとに変える追跡の設定。"""

    max_tilt: float  # 囲んだ向きから、この角度(度)まで傾けて探す
    tilt_step: float  # 傾きの刻み(度)
    search_margin: float  # 前フレームの位置から、物体の大きさの何倍まで動くと見込むか
    context: float  # 囲んだ範囲の外側を、手がかりに含める割合
    min_match: float  # この一致度以上のフレームだけ保存する
    max_count: int  # 1つの動画で囲める最大数(グローブは両手で2つ)
    prompt: str  # 囲む画面に出す説明


# バッグ以外は、動きが速い・向きが大きく変わる物体なので、探す範囲と傾きを広げ、一致度の基準をゆるめる
CLASS_SETTINGS = {
    BAG: ClassSettings(15.0, 1.5, 0.8, 0.15, 0.5, 1, "the punching bag"),
    GLOVE: ClassSettings(40.0, 5.0, 2.0, 0.1, 0.35, 2, "a boxing glove"),
    HEAD: ClassSettings(25.0, 2.5, 1.0, 0.15, 0.45, 1, "the person's head"),
    BODY: ClassSettings(20.0, 2.5, 0.8, 0.1, 0.45, 1, "the person's body (torso)"),
}


@dataclass
class Detection:
    """YOLO が検出した1つの物体。座標はすべて元フレームのもの。"""

    key: str  # BAG / GLOVE / HEAD / BODY
    conf: float
    box: tuple  # (x0, y0, x1, y1)
    polygon: Optional[np.ndarray] = None  # マスクの輪郭 (N, 2)。無ければ None

    @property
    def center(self):
        x0, y0, x1, y1 = self.box
        return (x0 + x1) / 2.0, (y0 + y1) / 2.0

    @property
    def height(self):
        return float(self.box[3] - self.box[1])

    def outline(self):
        """接触判定や描画に使う輪郭(整数座標, (N, 1, 2))。マスクが無ければ枠の四角形。"""
        if self.polygon is not None and len(self.polygon) >= 3:
            return np.round(self.polygon).astype(np.int32).reshape(-1, 1, 2)
        x0, y0, x1, y1 = (int(round(v)) for v in self.box)
        return np.array([[x0, y0], [x1, y0], [x1, y1], [x0, y1]], np.int32).reshape(-1, 1, 2)
