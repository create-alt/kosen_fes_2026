"""サンドバッグの検知と姿勢(傾き)推定(高速化版)。

高速化のポイント
1. 探索範囲の絞り込み : 前フレームのバッグ位置の周辺だけを処理する
                        (見失ったときだけ画面全体を探索する)
2. 縮小画像で解析     : 切り出した範囲を analysis_width 程度まで縮小して処理する

バッグらしい塊の選び方
色が近い塊が複数見つかることがあるので、面積の大きさだけでは選ばず、次の3つの
「らしさ」を掛け合わせた点数が最も高い塊をバッグとする。
- 大きさ : 学習時のバッグの面積(画面比)に近いか
- 形     : 学習時のバッグの細長さ(長軸/短軸)に近いか
- 位置   : 前フレームのバッグ位置から、飛びすぎていないか
点数が低すぎる塊しか無い場合は「見失った」とみなす。
"""

import math
from dataclasses import dataclass

import cv2
import numpy as np

from _log.before_refactaring.bag_model import backproject, elongation_from_moments

# back-projection の確率(0-255)をバッグ領域とみなすしきい値
PROB_THRESHOLD = 127

# --- 塊の選び方の設定 ---
MAX_CANDIDATES = 5  # 面積の大きい順に、この数までの塊だけを採点する
AREA_SIGMA = 0.7  # 面積比の許容幅(対数)。0.7 ≒ 面積が約2倍・半分でも減点は小さめ
ELONG_SIGMA = 0.5  # 細長さの比の許容幅(対数)
POSITION_SIGMA = 0.8  # 位置の許容幅(バッグの大きさに対する倍率)
POSITION_SIGMA_GROWTH = 0.3  # 見失っている間、1フレームごとに許容幅を広げる量
POSITION_MEMORY_FRAMES = 15  # これ以上見失ったら、前の位置は当てにしない
MIN_SHAPE_SCORE = 0.1  # 大きさ+形の点数がこれ未満の塊は、位置に関係なく除外
MIN_SCORE = 0.03  # 位置も含めた最終点数がこれ未満の塊はバッグとみなさない


@dataclass
class BagPose:
    """1フレームぶんのサンドバッグの姿勢。座標はすべて元フレームのもの。"""

    angle_deg: float  # 垂直からの傾き。+ は主軸の下端が画像の右へ、- は左へ振れた状態
    center: tuple  # バッグ重心 (x, y)
    axis: tuple  # 主軸の両端 ((x1, y1), (x2, y2))
    contour: np.ndarray  # バッグの輪郭
    score: float = 0.0  # 塊の選択に使った「バッグらしさ」の点数(0〜1)


def _log_gauss(ratio, sigma):
    """比 ratio が 1 に近いほど 1、離れるほど 0 に近づく(対数スケールの正規分布)。"""
    if ratio <= 0:
        return 0.0
    z = math.log(ratio) / sigma
    return math.exp(-0.5 * z * z)


class BagTracker:
    """学習済みモデルを使ってサンドバッグを追跡し、フレームごとに姿勢を推定する。"""

    def __init__(
        self,
        model,
        analysis_width=320,
        search_margin=0.5,
        smoothing=0.6,
        min_area=60,
    ):
        """
        model          : 学習済みのバッグ情報(bag_model.BagModel)
        analysis_width : 解析時の画像幅(px)。小さいほど速いが粗くなる
        search_margin  : 前フレームのバッグ外接矩形を、何倍ぶん外側へ広げて探すか
                         (バッグが次のフレームまでに動く距離を見込む)
        smoothing      : 角度の平滑化係数(0 に近いほど滑らか、1 で平滑化なし)
        min_area       : バッグ候補とみなす最小面積(縮小画像上の px)
        """
        self._hist = model.hist
        self._ref_area = model.area_ratio
        self._ref_elong = model.elongation
        self.analysis_width = analysis_width
        self.search_margin = search_margin
        self.smoothing = smoothing
        self.min_area = min_area

        self._scale = None  # 最初のフレームの幅から決める
        self._angle = None
        self._next_window = None  # 次に探索する範囲(None なら画面全体)
        self.used_window = None  # 直近のフレームで実際に探索した範囲(描画用)
        self._last_center = None  # 直近に見つけたバッグの重心(元フレーム座標)
        self._last_size = None  # 直近に見つけたバッグの大きさ(外接矩形の長辺、px)
        self._lost_frames = 0  # 連続で見失っているフレーム数
        self._kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (3, 3))

    def seed(self, region, frame_shape):
        """
        追跡の開始位置を指定する。最初のフレームで、画面全体ではなく
        指定した領域(x, y, 幅, 高さ)の周辺からバッグを探すようになる。
        """
        frame_h, frame_w = frame_shape[:2]
        x, y, width, height = region
        margin_x, margin_y = width * self.search_margin, height * self.search_margin
        self._next_window = (
            max(0, int(x - margin_x)),
            max(0, int(y - margin_y)),
            min(frame_w, int(x + width + margin_x)),
            min(frame_h, int(y + height + margin_y)),
        )
        self._last_center = (x + width / 2, y + height / 2)
        self._last_size = max(width, height)
        self._lost_frames = 0

    def track(self, frame):
        """現在フレームからバッグ姿勢を推定する。見失ったら None を返す。"""
        frame_h, frame_w = frame.shape[:2]
        if self._scale is None:
            self._scale = min(1.0, self.analysis_width / frame_w)

        # 1) 探索範囲を切り出し、2) 縮小する(先に切り出すので縮小コストも小さい)
        x0, y0, x1, y1 = self._next_window or (0, 0, frame_w, frame_h)
        self.used_window = (x0, y0, x1, y1)
        crop = frame[y0:y1, x0:x1]
        crop_h, crop_w = crop.shape[:2]
        small_w = max(1, int(round(crop_w * self._scale)))
        small_h = max(1, int(round(crop_h * self._scale)))
        if small_w < 8 or small_h < 8:
            self._mark_lost()
            return None
        small = cv2.resize(
            crop, (small_w, small_h), interpolation=cv2.INTER_AREA
        )
        rx, ry = crop_w / small_w, crop_h / small_h  # 縮小画像 → 元フレームの倍率

        hsv = cv2.cvtColor(small, cv2.COLOR_BGR2HSV)
        prob = backproject(hsv, self._hist)
        prob = cv2.blur(prob, (3, 3))
        _, mask = cv2.threshold(prob, PROB_THRESHOLD, 255, cv2.THRESH_BINARY)
        mask = cv2.morphologyEx(mask, cv2.MORPH_OPEN, self._kernel)

        contours, _ = cv2.findContours(
            mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE
        )
        picked = self._pick_bag(contours, x0, y0, rx, ry, frame_w * frame_h)
        if picked is None:
            self._mark_lost()  # 次は画面全体から探す
            return None
        contour, moments, score = picked

        cx = moments["m10"] / moments["m00"]
        cy = moments["m01"] / moments["m00"]

        # 画像モーメントから主軸(長軸)の向きを求める
        mu20 = moments["mu20"] / moments["m00"]
        mu02 = moments["mu02"] / moments["m00"]
        mu11 = moments["mu11"] / moments["m00"]
        theta = 0.5 * math.atan2(2 * mu11, mu20 - mu02)  # x軸からの角度

        angle = self._smooth(self._tilt_from_vertical(theta))

        # 縮小画像上の結果を元フレームの座標へ戻す
        bx, by, bw, bh = cv2.boundingRect(contour)
        center = (x0 + cx * rx, y0 + cy * ry)
        full_w, full_h = bw * rx, bh * ry
        half_len = 0.5 * max(full_w, full_h)
        dx, dy = math.cos(theta) * half_len, math.sin(theta) * half_len
        axis = (
            (int(center[0] - dx), int(center[1] - dy)),
            (int(center[0] + dx), int(center[1] + dy)),
        )
        contour_full = (
            contour.astype(np.float32) * np.array([rx, ry], np.float32)
            + np.array([x0, y0], np.float32)
        ).astype(np.int32)

        # 次フレームの探索範囲 = 今回のバッグ外接矩形 + 余白
        full_x, full_y = x0 + bx * rx, y0 + by * ry
        margin_x, margin_y = full_w * self.search_margin, full_h * self.search_margin
        self._next_window = (
            max(0, int(full_x - margin_x)),
            max(0, int(full_y - margin_y)),
            min(frame_w, int(full_x + full_w + margin_x)),
            min(frame_h, int(full_y + full_h + margin_y)),
        )

        self._last_center = center
        self._last_size = max(full_w, full_h)
        self._lost_frames = 0

        return BagPose(angle, center, axis, contour_full, score)

    def _mark_lost(self):
        self._next_window = None
        self._lost_frames += 1

    def _pick_bag(self, contours, x0, y0, rx, ry, frame_area):
        """
        候補の塊を採点し、最も点数の高いものを (輪郭, モーメント, 点数) で返す。
        点数が MIN_SCORE 未満、または候補が無い場合は None。
        """
        candidates = sorted(
            (c for c in contours if cv2.contourArea(c) >= self.min_area),
            key=cv2.contourArea,
            reverse=True,
        )[:MAX_CANDIDATES]
        if not candidates:
            return None

        has_shape_prior = self._ref_area is not None and self._ref_elong is not None
        max_area = cv2.contourArea(candidates[0])
        use_position = (
            self._last_center is not None
            and self._lost_frames <= POSITION_MEMORY_FRAMES
        )

        best = None
        for contour in candidates:
            m = cv2.moments(contour)
            if m["m00"] <= 0:
                continue

            if has_shape_prior:
                area_ratio = m["m00"] * rx * ry / frame_area
                score = _log_gauss(area_ratio / self._ref_area, AREA_SIGMA)
                score *= _log_gauss(
                    elongation_from_moments(m) / self._ref_elong, ELONG_SIGMA
                )
                # 大きさ・形がバッグと合わない塊は、位置が近くても採用しない。
                # (見失っている間は位置の許容幅が広がるので、形で歯止めをかける)
                if score < MIN_SHAPE_SCORE:
                    continue
            else:
                # 形の情報が無い古いモデルでは、従来どおり大きい塊を優先する
                score = m["m00"] / max_area

            if use_position:
                cx = x0 + m["m10"] / m["m00"] * rx
                cy = y0 + m["m01"] / m["m00"] * ry
                distance = math.hypot(
                    cx - self._last_center[0], cy - self._last_center[1]
                )
                sigma = self._last_size * (
                    POSITION_SIGMA + POSITION_SIGMA_GROWTH * self._lost_frames
                )
                score *= math.exp(-0.5 * (distance / sigma) ** 2)

            if best is None or score > best[2]:
                best = (contour, m, score)

        if best is None or best[2] < MIN_SCORE:
            return None
        return best

    @staticmethod
    def _tilt_from_vertical(theta):
        """x軸からの角度 theta を、垂直からの傾き(-90〜90度)に変換する。"""
        angle = 90.0 - math.degrees(theta)
        while angle > 90.0:
            angle -= 180.0
        while angle <= -90.0:
            angle += 180.0
        return angle

    def _smooth(self, angle):
        """指数移動平均でノイズを抑える。"""
        if self._angle is None:
            self._angle = angle
        else:
            a = self.smoothing
            self._angle = a * angle + (1 - a) * self._angle
        return self._angle
