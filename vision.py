"""画像から読み取る部分: サンドバッグの領域・傾きの推定(YOLO セグメンテーション)と、
人の骨格推定(YOLO-pose)。この2つは独立しているので、Perception で並列に実行する。

    perception = Perception(BagTracker(...), PoseEstimator(...))
    bag, person = perception.process(frame)     # BagPose / PersonPose(どちらも検出できなければ None)

GPU を使うときは common.resolve_device() で決めた Device を渡す(FP16 も自動で有効になる)。
"""

import math
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
from pathlib import Path
from typing import Optional

import cv2
import numpy as np

from common import HEAD_POINTS, HIPS, SHOULDERS, WRISTS

DEFAULT_BAG_WEIGHTS = Path(__file__).with_name("bag_yolo.pt")  # train_yolo.py が作る
DEFAULT_POSE_WEIGHTS = Path(__file__).with_name("pose_model.pt")  # train.py が作る
BASE_POSE_WEIGHTS = "yolo11n-pose.pt"  # 標準の骨格モデル(無ければ自動でダウンロードされる)
BODY_POINTS = SHOULDERS + HIPS


def prefer_offline(*weights):
    """使うモデルがすべて手元にあるなら、ultralytics のネット接続確認(起動時の DNS 問い合わせなど)を省く。
    ネットが遅い・繋がらない環境で、起動が数秒止まるのを防ぐ。import ultralytics より前に呼ぶ。"""
    import os, sys

    if "ultralytics" not in sys.modules and all(w and Path(str(w)).is_file() for w in weights):
        os.environ.setdefault("YOLO_OFFLINE", "1")


def _load_yolo(weights, task, hint):
    try:
        from ultralytics import YOLO
    except ImportError:
        raise SystemExit("ultralytics is not installed. Run: pip install ultralytics")
    try:
        model = YOLO(str(weights))
    except Exception as error:  # ファイルが無い・ダウンロード失敗など
        raise SystemExit(f"Could not load '{weights}': {error}\n{hint}")
    if getattr(model, "task", task) != task:
        raise SystemExit(f"'{weights}' is a '{model.task}' model; a '{task}' model is required.\n{hint}")
    return model


def _fp16_args():
    """FP16 推論の指定。ultralytics の新しい版は quantize=16、古い版は half=True(FP32 のときは何も渡さない)。"""
    from ultralytics.cfg import DEFAULT_CFG_DICT

    return {"quantize": 16} if "quantize" in DEFAULT_CFG_DICT else {"half": True}


def _predict(model, frame, device, imgsz, conf, **extra):
    if device.half:
        extra.update(_fp16_args())
    return model.predict(frame, imgsz=imgsz, conf=conf, device=device.name, verbose=False, **extra)[0]


# ------------------------------------------------------------------ サンドバッグ


@dataclass
class BagPose:
    """1フレームぶんのサンドバッグの姿勢。座標はすべて元フレームの画素。"""

    angle_deg: float  # 垂直からの傾き。+ は下端が画像の右へ、- は左へ振れた状態
    center: tuple  # 重心 (x, y)
    axis: tuple  # 主軸の両端 ((x1, y1), (x2, y2))
    contour: np.ndarray  # 領域の輪郭 (N, 1, 2) int32
    box: tuple  # 検出枠 (x0, y0, x1, y1)


class BagTracker:
    """YOLO セグメンテーションでバッグの領域(マスク)を求め、主軸から傾きを推定する。"""

    def __init__(self, weights=DEFAULT_BAG_WEIGHTS, device=None, imgsz=320, conf=0.4, smoothing=0.6):
        hint = "Create it first: python collect_dataset.py bag.mp4 → python train_yolo.py"
        if not Path(weights).exists():
            raise SystemExit(f"Bag weights not found: {weights}\n{hint}")
        self._model = _load_yolo(weights, "segment", hint)
        self.device, self.imgsz, self.conf, self.smoothing = device, imgsz, conf, smoothing

        # クラスはバッグだけを使う(以前のグローブ・頭・体入りのモデルも、バッグ以外は無視する)
        names = {int(i): str(n).lower() for i, n in (self._model.names or {}).items()}
        self._classes = [i for i, n in names.items() if "bag" in n] or (
            list(names) if len(names) == 1 else None
        )
        if self._classes is None:
            raise SystemExit(f"The model has no bag class (classes: {list(names.values())}).")
        self._angle = None
        _predict(self._model, np.zeros((imgsz, imgsz, 3), np.uint8), device, imgsz, conf)  # 初回の遅さを先に

    def track(self, frame) -> Optional[BagPose]:
        result = _predict(self._model, frame, self.device, self.imgsz, self.conf,
                          classes=self._classes, max_det=5)
        if result.boxes is None or len(result.boxes) == 0 or result.masks is None:
            return None
        best = int(result.boxes.conf.argmax())
        polygon = result.masks.xy[best]
        if len(polygon) < 3:
            return None
        contour = np.round(polygon).astype(np.int32).reshape(-1, 1, 2)
        m = cv2.moments(contour)
        if m["m00"] <= 0:
            return None

        # 画像モーメントの主軸(長軸)の向きから、垂直からの傾きを求める
        cx, cy = m["m10"] / m["m00"], m["m01"] / m["m00"]
        theta = 0.5 * math.atan2(2 * m["mu11"] / m["m00"], (m["mu20"] - m["mu02"]) / m["m00"])
        angle = 90.0 - math.degrees(theta)
        angle = (angle + 90.0) % 180.0 - 90.0  # -90〜90度に収める
        self._angle = angle if self._angle is None else (
            self.smoothing * angle + (1 - self.smoothing) * self._angle
        )

        _, _, bw, bh = cv2.boundingRect(contour)
        half = 0.5 * max(bw, bh)
        dx, dy = math.cos(theta) * half, math.sin(theta) * half
        axis = ((int(cx - dx), int(cy - dy)), (int(cx + dx), int(cy + dy)))
        box = tuple(float(v) for v in result.boxes.xyxy[best].cpu().numpy())
        return BagPose(self._angle, (cx, cy), axis, contour, box)


# ------------------------------------------------------------------ 人の骨格


def resolve_pose_weights(weights=None):
    """指定があればそれ。無ければ pose_model.pt(あれば)、なければ標準の yolo11n-pose.pt。"""
    if weights:
        return str(weights)
    return str(DEFAULT_POSE_WEIGHTS) if DEFAULT_POSE_WEIGHTS.exists() else BASE_POSE_WEIGHTS


def _mean(points):
    return (float(np.mean([p[0] for p in points])), float(np.mean([p[1] for p in points]))) if points else None


@dataclass
class PersonPose:
    """1人ぶんの骨格推定の結果。座標はすべて元フレームの画素。"""

    keypoints: np.ndarray  # (17, 3): x, y, 信頼度
    box: tuple  # (x0, y0, x1, y1)
    conf: float  # 人の検出の信頼度
    kpt_conf: float = 0.5  # 点を「見えている」とみなす信頼度のしきい値

    def point(self, index) -> Optional[tuple]:
        x, y, c = self.keypoints[index]
        return (float(x), float(y)) if c >= self.kpt_conf else None

    def visible_points(self, indices):
        """見えている点 {番号: (x, y)}。"""
        return {i: p for i in indices if (p := self.point(i)) is not None}

    @property
    def head(self):
        """頭の位置。鼻・目・耳のうち見える点の平均。見えなければ None。"""
        return _mean(list(self.visible_points(HEAD_POINTS).values()))

    def _mid(self, indices):
        return _mean(list(self.visible_points(indices).values()))

    @property
    def body_center(self):
        """体の重心。肩の中点と腰の中点の中央。どちらかが見えなければ None。"""
        shoulder, hip = self._mid(SHOULDERS), self._mid(HIPS)
        return None if shoulder is None or hip is None else _mean([shoulder, hip])

    @property
    def torso_length(self):
        """肩の中点〜腰の中点の距離(動き量を人の大きさで割って比べるための目安)。"""
        shoulder, hip = self._mid(SHOULDERS), self._mid(HIPS)
        if shoulder is None or hip is None:
            return None
        length = math.hypot(shoulder[0] - hip[0], shoulder[1] - hip[1])
        return length if length > 1e-6 else None

    @property
    def hands(self):
        """両手の位置(手首)。見えている手だけ。"""
        return list(self.visible_points(WRISTS).values())

    @property
    def hands_by_side(self):
        """見えている手首 {"L": (x, y), "R": (x, y)}(L = 人の左手、R = 右手)。"""
        found = self.visible_points(WRISTS)
        return {side: found[i] for side, i in zip(("L", "R"), WRISTS) if i in found}


class PoseEstimator:
    """YOLO-pose で、フレームに写る人のうち最も大きい1人の骨格を推定する。"""

    def __init__(self, weights=None, device=None, imgsz=640, conf=0.25, kpt_conf=0.5):
        self.weights = resolve_pose_weights(weights)
        self._model = _load_yolo(
            self.weights, "pose", "Use yolo11n-pose.pt, or a model trained with train.py."
        )
        self.device, self.imgsz, self.conf, self.kpt_conf = device, imgsz, conf, kpt_conf
        _predict(self._model, np.zeros((imgsz, imgsz, 3), np.uint8), device, imgsz, conf)

    def people(self, frame):
        """検出した全員の PersonPose(大きい順)。"""
        result = _predict(self._model, frame, self.device, self.imgsz, self.conf)
        if result.boxes is None or len(result.boxes) == 0 or result.keypoints is None:
            return []
        boxes = result.boxes.xyxy.cpu().numpy()
        confs = result.boxes.conf.cpu().numpy()
        keypoints = result.keypoints.data.cpu().numpy().astype(np.float32)  # (n, 17, 3)
        if keypoints.shape[1] != 17:
            raise SystemExit(f"The pose model must have 17 keypoints (got {keypoints.shape[1]}).")
        areas = (boxes[:, 2] - boxes[:, 0]) * (boxes[:, 3] - boxes[:, 1])
        return [
            PersonPose(keypoints[i], tuple(float(v) for v in boxes[i]), float(confs[i]), self.kpt_conf)
            for i in np.argsort(-areas)
        ]

    def estimate(self, frame) -> Optional[PersonPose]:
        found = self.people(frame)
        return found[0] if found else None


# ------------------------------------------------------------------ 並列実行


class Perception:
    """
    バッグの推定と骨格推定を、同じフレームに対して並列に行う。どちらも PyTorch の推論で、
    計算中は Python のロックを手放すので、スレッドで重ねると速くなる(別のモデルなので衝突しない)。
    """

    def __init__(self, bag: BagTracker, pose: Optional[PoseEstimator] = None, parallel=True):
        self.bag, self.pose = bag, pose
        self._pool = ThreadPoolExecutor(max_workers=1) if (pose and parallel) else None

    def process(self, frame):
        """(BagPose または None, PersonPose または None)。"""
        if self.pose is None:
            return self.bag.track(frame), None
        if self._pool is None:
            return self.bag.track(frame), self.pose.estimate(frame)
        future = self._pool.submit(self.pose.estimate, frame)  # 骨格は別スレッドで
        bag = self.bag.track(frame)  # バッグは、このスレッドで
        return bag, future.result()

    def close(self):
        if self._pool is not None:
            self._pool.shutdown(wait=False)
