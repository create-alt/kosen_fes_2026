"""YOLO(セグメンテーション)で毎フレームバッグを検出し、傾きを推定する。

色追跡(bag_pose.BagTracker)と同じ使い方ができる:
    pose = tracker.track(frame)   # BagPose または None
    tracker.used_window           # 描画用の検出枠 (x0, y0, x1, y1)
    tracker.detections            # 全クラスの検出 {"bag": [...], "glove": [...], "head": [...], "body": [...]}
    tracker.supports("glove")     # 学習済みモデルがそのクラスを持っているか

YOLO はバッグの「マスク(輪郭)」を返し、その主軸が垂直からどれだけ傾いているかを
色追跡と同じ計算で求める。バウンディングボックスだけでは傾きが分からないため、
検出(detect)ではなくセグメンテーション(segment)のモデルが必要。

標準の学習済みモデル(COCO)には「サンドバッグ」のクラスが無いので、
collect_dataset.py → train_yolo.py で、お手元のバッグ用に学習したモデルを使う。
"""

import math
from pathlib import Path

import cv2
import numpy as np

from bag_pose import BagPose, BagTracker
from _log.before_refactaring.classes import BAG, BODY, GLOVE, HEAD, Detection, key_from_model_name

# 1フレームで採用する数。(グローブは両手ぶんの2つ。それ以外は1つ)
MAX_PER_CLASS = {BAG: 1, GLOVE: 2, HEAD: 1, BODY: 1}

DEFAULT_YOLO_WEIGHTS = Path(__file__).with_name("bag_yolo.pt")


class YoloBagTracker:
    """
    YOLO セグメンテーションで、サンドバッグ・グローブ・頭・体を検出する。
    サンドバッグについては、フレームごとに姿勢(傾き)も推定する。
    """

    def __init__(
        self,
        weights=DEFAULT_YOLO_WEIGHTS,
        imgsz=320,
        conf=0.4,
        device=None,
        smoothing=0.6,
    ):
        """
        weights   : 学習済みのセグメンテーションモデル(.pt, .onnx など)
        imgsz     : 推論時の画像サイズ。小さいほど速い
        conf      : 検出の信頼度のしきい値
        device    : "cpu", "0"(GPU), "mps" など。None なら自動
        smoothing : 角度の平滑化係数(0 に近いほど滑らか、1 で平滑化なし)
        """
        weights = Path(weights)
        if not weights.exists():
            raise SystemExit(
                f"YOLO weights not found: {weights}\n"
                "Create them first:\n"
                "  1) python collect_dataset.py bag.mp4   (make training data)\n"
                "  2) python train_yolo.py                (train and save bag_yolo.pt)"
            )
        try:
            from ultralytics import YOLO
        except ImportError:
            raise SystemExit(
                "ultralytics is not installed. Run: pip install ultralytics"
            )

        self._model = YOLO(str(weights))
        if getattr(self._model, "task", "segment") != "segment":
            raise SystemExit(
                "The YOLO model must be a segmentation model (e.g. yolo11n-seg), "
                "because the tilt is computed from the bag's mask."
            )

        self.imgsz = imgsz
        self.conf = conf
        self.device = device
        self.smoothing = smoothing

        # モデルのクラス番号 → BAG / GLOVE / HEAD / BODY。知らない名前のクラスは無視する
        names = getattr(self._model, "names", {}) or {}
        self._class_keys = {int(i): key_from_model_name(n) for i, n in names.items()}
        self.available = {k for k in self._class_keys.values() if k is not None}
        if BAG not in self.available:
            raise SystemExit(
                f"The model has no bag class (classes: {list(names.values())}). "
                "Retrain with train_yolo.py."
            )

        self._angle = None
        self.used_window = None  # 直近のフレームで検出した枠(描画用)
        self.detections = {key: [] for key in MAX_PER_CLASS}  # 直近のフレームの全クラスの検出

        # 初回推論は遅い(初期化がある)ので、ここで済ませておく
        self._predict(np.zeros((imgsz, imgsz, 3), np.uint8))

    def _predict(self, frame):
        return self._model.predict(
            frame,
            imgsz=self.imgsz,
            conf=self.conf,
            device=self.device,
            max_det=20,
            verbose=False,
        )[0]

    def supports(self, key):
        """学習済みモデルが、そのクラス(BAG / GLOVE / HEAD / BODY)を持っているか。"""
        return key in self.available

    def _collect(self, result):
        """検出結果を、クラスごとに信頼度の高い順に並べる(採用数はクラスごとに MAX_PER_CLASS)。"""
        found = {key: [] for key in MAX_PER_CLASS}
        if result.boxes is None or len(result.boxes) == 0:
            return found

        confs = result.boxes.conf.cpu().numpy()
        classes = result.boxes.cls.cpu().numpy().astype(int)
        boxes = result.boxes.xyxy.cpu().numpy()
        polygons = result.masks.xy if result.masks is not None else None

        for index in np.argsort(-confs):
            key = self._class_keys.get(int(classes[index]))
            if key is None or len(found[key]) >= MAX_PER_CLASS[key]:
                continue
            polygon = None
            if polygons is not None and index < len(polygons) and len(polygons[index]) >= 3:
                polygon = np.asarray(polygons[index], np.float32)
            if key == BAG and polygon is None:
                continue  # バッグはマスクが無いと傾きを求められない
            x0, y0, x1, y1 = (float(v) for v in boxes[index])
            found[key].append(Detection(key, float(confs[index]), (x0, y0, x1, y1), polygon))
        return found

    def track(self, frame):
        """現在フレームからバッグ姿勢を推定する。検出できなければ None を返す。"""
        result = self._predict(frame)
        self.used_window = None
        self.detections = self._collect(result)

        bags = self.detections[BAG]
        if not bags:
            return None
        bag = bags[0]  # 信頼度が最も高いバッグ

        contour = bag.outline()
        moments = cv2.moments(contour)
        if moments["m00"] <= 0:
            return None

        cx = moments["m10"] / moments["m00"]
        cy = moments["m01"] / moments["m00"]

        # 画像モーメントから主軸(長軸)の向きを求める(色追跡と同じ計算)
        mu20 = moments["mu20"] / moments["m00"]
        mu02 = moments["mu02"] / moments["m00"]
        mu11 = moments["mu11"] / moments["m00"]
        theta = 0.5 * math.atan2(2 * mu11, mu20 - mu02)  # x軸からの角度

        angle = self._smooth(BagTracker._tilt_from_vertical(theta))

        _, _, bw, bh = cv2.boundingRect(contour)
        half_len = 0.5 * max(bw, bh)
        dx, dy = math.cos(theta) * half_len, math.sin(theta) * half_len
        axis = (
            (int(cx - dx), int(cy - dy)),
            (int(cx + dx), int(cy + dy)),
        )

        x0, y0, x1, y1 = (int(v) for v in bag.box)
        self.used_window = (x0, y0, x1, y1)

        return BagPose(angle, (cx, cy), axis, contour, bag.conf)

    def _smooth(self, angle):
        """指数移動平均でノイズを抑える。"""
        if self._angle is None:
            self._angle = angle
        else:
            a = self.smoothing
            self._angle = a * angle + (1 - a) * self._angle
        return self._angle
