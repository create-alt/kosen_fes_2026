"""サンドバッグ用の YOLO(セグメンテーション)教師データを作る。

動画(またはカメラ)ごとに、最初の画面でバッグを囲む。**囲んだ範囲に映っているものをバッグとして
扱い**、以降のフレームでテンプレートマッチングで追いかけて、YOLO 形式のラベルとして保存する。
(人の頭・体・手は骨格推定で求めるので、ここでは囲まない。)

    python collect_dataset.py bag1.mp4                    # 動画から作る
    python collect_dataset.py bag1.mp4 bag2.mp4 bag3.mp4  # 複数の動画から(動画ごとに囲む)
    python collect_dataset.py 0                           # カメラ0から作る(q で終了)
    python collect_dataset.py bag1.mp4 --fresh            # 前のデータを消して作り直す

操作
    1. 表示された画面で、バッグの四隅を4回クリックして Enter(Backspace で1つ戻す、r でやり直し)
    2. プレビューが出るので、輪郭がバッグに沿っていれば Enter で開始
         r : 囲み直す   s : この動画を飛ばす   q : 全体を終了
    3. 収集中は q で終了

囲み方のコツ
    - バッグがはっきり見える(動きの少ない)フレームで囲む(--start-frame で開始位置を選べる)
    - バッグだけを囲み、背景を含めない。境目ぎりぎりで囲む(バッグ全体でなくクッション部分だけでもよい)
    - 保存されるのは、囲んだ範囲との一致度が --min-match 以上のフレームだけ

追い方: 囲んだ範囲(と周りの少しの背景)を ±max_tilt 度まで傾けた画像を用意し、各フレームで一致度が
最も高い位置と傾きを選ぶ。照合の前に局所コントラスト正規化をかけるので、影や照明のむらに強い。
ラベルは、囲んだ四角形を見つけた位置・傾きに合わせて動かした四角形。
良いモデルにするコツ: 照明、背景、パンチの強さ、バッグの振れ方を変えた映像をいくつか用意する。
"""

import argparse
import math
import shutil
from collections import namedtuple
from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np

from common import write_image

WINDOW_NAME = "Collecting dataset (press Q to stop)"
PREVIEW_NAME = "Check the outline: ENTER = start, R = reselect, S = skip, Q = quit"
DEFAULT_OUT = Path(__file__).with_name("dataset")
SELECT_VIEW_SIZE = (1280, 720)  # 囲む画面・確認画面の最大サイズ(これより大きい映像は縮小して表示)
TRACK_MIN_SCORE = 0.35  # これ未満の一致度のときは「見失った」として探索範囲を広げる
GREEN = (0, 255, 0)

Pose = namedtuple("Pose", "angle_deg contour score")  # 追跡結果: 真っすぐからの傾き(度)・輪郭(4点)・一致度


# ---------------------------------------------------------------- 画面での選択


def fit_to_screen(frame, max_size=SELECT_VIEW_SIZE):
    """画面に収まる大きさに縮小した画像と、その縮小率を返す。"""
    height, width = frame.shape[:2]
    scale = min(1.0, max_size[0] / width, max_size[1] / height)
    if scale >= 1.0:
        return frame, 1.0
    return cv2.resize(frame, None, fx=scale, fy=scale, interpolation=cv2.INTER_AREA), scale


def _close(window):
    cv2.destroyWindow(window)
    cv2.waitKey(1)  # ウィンドウを確実に閉じる


def select_rect_or_none(frame, window_name="Select the bag, then press ENTER/SPACE"):
    """ドラッグで長方形を選ぶ。結果は元の映像の座標 (x, y, 幅, 高さ)。キャンセルなら None。"""
    view, scale = fit_to_screen(frame)
    x, y, w, h = cv2.selectROI(window_name, view, showCrosshair=True, fromCenter=False)
    _close(window_name)
    if w == 0 or h == 0:
        return None
    return tuple(int(round(v / scale)) for v in (x, y, w, h))


def select_quad_or_none(frame, window_name="Click the 4 corners of the bag", target="the bag"):
    """
    四隅を4回クリックして、斜めを向いた四角形で選ぶ(傾いたバッグでも背景を含めずに囲める)。
    左クリック=点を追加 / Backspace か Z=1つ戻す / R=やり直し / Enter か Space=決定 / Esc か Q=キャンセル。
    結果は元の映像の座標の 4点 (float32, 4x2)。キャンセルなら None。点の順番は問わない。
    """
    view, scale = fit_to_screen(frame)
    points = []

    def on_mouse(event, x, y, flags, param):
        if event == cv2.EVENT_LBUTTONDOWN and len(points) < 4:
            points.append((x, y))

    cv2.namedWindow(window_name, cv2.WINDOW_AUTOSIZE)
    cv2.setMouseCallback(window_name, on_mouse)
    result = None
    try:
        while True:
            canvas = view.copy()
            if len(points) >= 2:
                shape = np.array(points, np.int32).reshape(-1, 1, 2)
                if len(points) == 4:
                    shape = cv2.convexHull(shape)  # クリックの順番によらず、交差しない四角形にする
                cv2.polylines(canvas, [shape], len(points) == 4, (0, 255, 255), 1)
            for i, point in enumerate(points):
                cv2.circle(canvas, point, 4, (0, 0, 255), -1)
                cv2.putText(canvas, str(i + 1), (point[0] + 6, point[1] - 6),
                            cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 0, 255), 2)
            message = f"Click the 4 corners of {target} ({len(points)}/4)" if len(points) < 4 else "ENTER = decide"
            cv2.putText(canvas, message, (10, 28), cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 255, 255), 2)
            cv2.putText(canvas, "Backspace = undo / R = reset / Q = cancel", (10, 56),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.6, (255, 255, 255), 2)
            cv2.imshow(window_name, canvas)

            key = cv2.waitKey(30) & 0xFF
            if key in (13, 10, 32) and len(points) == 4:
                result = (np.array(points, np.float32) / scale).reshape(4, 2)
                break
            elif key in (8, 127, ord("z")) and points:
                points.pop()
            elif key == ord("r"):
                points.clear()
            elif key in (27, ord("q")):
                break
    finally:
        _close(window_name)
    return result


# ---------------------------------------------------------------- テンプレートマッチングによる追跡


def quad_from_region(region):
    """(x, y, 幅, 高さ) の長方形、または 4点の座標から、4点の配列(float32, 4x2)にする。"""
    arr = np.asarray(region, np.float32)
    if arr.shape == (4,):
        x, y, w, h = arr
        return np.array([[x, y], [x + w, y], [x + w, y + h], [x, y + h]], np.float32)
    return arr.reshape(4, 2).copy()


def order_quad(points):
    """4点を 左上, 右上, 右下, 左下 の順に並べる(クリックした順番は問わない)。"""
    pts = np.asarray(points, np.float32).reshape(4, 2)
    by_y = pts[np.argsort(pts[:, 1])]
    top = by_y[:2][np.argsort(by_y[:2, 0])]
    bottom = by_y[2:][np.argsort(by_y[2:, 0])]
    return np.array([top[0], top[1], bottom[1], bottom[0]], np.float32)


def crop_padded(frame, x0, y0, x1, y1):
    """frame[y0:y1, x0:x1] を切り出す。画面の外にはみ出した部分は、端の画素で埋める。"""
    frame_h, frame_w = frame.shape[:2]
    ix0, iy0, ix1, iy1 = max(0, x0), max(0, y0), min(frame_w, x1), min(frame_h, y1)
    if ix1 <= ix0 or iy1 <= iy0:  # 範囲が完全に画面の外
        return np.zeros((max(1, y1 - y0), max(1, x1 - x0)) + frame.shape[2:], frame.dtype)
    crop = frame[iy0:iy1, ix0:ix1]
    pad = (iy0 - y0, y1 - iy1, ix0 - x0, x1 - ix1)  # 上, 下, 左, 右
    return cv2.copyMakeBorder(crop, *pad, cv2.BORDER_REPLICATE) if any(pad) else crop


class TemplateTracker:
    """囲んだ範囲との一致度で、バッグの位置と傾き(基準から ±max_tilt 度)を探して追跡する。"""

    def __init__(self, first_frame, region, max_tilt=15.0, tilt_step=1.5, context=0.15,
                 search_margin=0.8, normalize=True, work_size=100, norm_sigma=6.0):
        """
        region        : 囲んだ範囲(長方形、または 4点)。**この向きを基準(探す傾きの中心)とする**
        context       : 囲んだ範囲の外側を何割ぶん手がかりに含めるか(無地のバッグでも境目が手がかりになる)
        search_margin : 前フレームの位置から、バッグの大きさの何倍まで動くと見込むか
        work_size     : 解析時に、囲んだ範囲の長辺をこの px 程度に縮小する(小さいほど速い)
        normalize     : True なら照合前に局所コントラスト正規化をかけ、影や照明のむらを打ち消す
        norm_sigma    : 正規化で「周辺」とみなす範囲(縮小後の px)
        """
        frame_h, frame_w = first_frame.shape[:2]
        quad = quad_from_region(region)
        quad[:, 0] = np.clip(quad[:, 0], 0, frame_w - 1)
        quad[:, 1] = np.clip(quad[:, 1], 0, frame_h - 1)
        top_left, top_right, bottom_right, bottom_left = quad = order_quad(quad)

        w = (np.linalg.norm(top_right - top_left) + np.linalg.norm(bottom_right - bottom_left)) / 2
        h = (np.linalg.norm(bottom_left - top_left) + np.linalg.norm(bottom_right - top_right)) / 2
        if w < 12 or h < 12:
            raise SystemExit("Selected area is too small. Select the bag again.")

        centroid = quad.mean(axis=0)
        # 選択した四角形の縦方向(上辺の中点 → 下辺の中点)の傾きが基準(探す傾き 0)
        axis_vec = (bottom_left + bottom_right) / 2 - (top_left + top_right) / 2
        self.base_angle = math.degrees(math.atan2(axis_vec[0], axis_vec[1]))
        self.max_tilt = max_tilt
        self.size = (float(w), float(h))
        self.center0 = (float(centroid[0]), float(centroid[1]))
        self.corner_offsets = quad - centroid  # 中心から見た四隅(傾き 0 のとき)
        self.search_margin, self.normalize, self.norm_sigma, self.context = (
            search_margin, normalize, norm_sigma, context)
        self.scale = min(1.0, work_size / max(w, h))

        # どの傾きでも収まる大きさの照合の箱
        reach = np.linalg.norm(self.corner_offsets * (1 + 2 * context), axis=1).max()
        self.tpl_w = self.tpl_h = max(8, int(math.ceil(2 * reach * self.scale)) + 2)
        steps = int(round(max_tilt / tilt_step))
        self.angles = [i * tilt_step for i in range(-steps, steps + 1)]

        self._center = self.center0  # 直近のバッグ中心(元フレームの座標)
        self._lost = 0  # 連続で見失っているフレーム数
        self._templates, self._masks = self._build_templates(first_frame)

    def _prepare(self, image):
        """照合用の画像にする(グレー + 軽いぼかし + 局所コントラスト正規化)。"""
        gray = cv2.GaussianBlur(cv2.cvtColor(image, cv2.COLOR_BGR2GRAY), (3, 3), 0)
        if not self.normalize:
            return gray
        gray = gray.astype(np.float32) / 255.0
        mean = cv2.GaussianBlur(gray, (0, 0), self.norm_sigma)
        variance = cv2.GaussianBlur((gray - mean) ** 2, (0, 0), self.norm_sigma)
        return (gray - mean) / np.sqrt(variance + 0.02 * 0.02)  # 0.02: 無地の部分でノイズを増幅しない

    def _build_templates(self, first_frame):
        """囲んだ範囲を、各角度に傾けたテンプレート(と、四角形の内側だけのマスク)を作る。"""
        s = self.scale
        small = cv2.resize(first_frame, None, fx=s, fy=s, interpolation=cv2.INTER_AREA) if s < 1.0 else first_frame
        gray = self._prepare(small)
        pad = max(self.tpl_w, self.tpl_h)  # 傾けても画面の外が写り込まないように余白をつける
        gray = cv2.copyMakeBorder(gray, pad, pad, pad, pad, cv2.BORDER_REPLICATE)
        cx, cy = self.center0[0] * s + pad, self.center0[1] * s + pad
        x0, y0 = int(round(cx - self.tpl_w / 2.0)), int(round(cy - self.tpl_h / 2.0))
        self._offset = (cx - (x0 + self.tpl_w / 2.0), cy - (y0 + self.tpl_h / 2.0))  # 整数に丸めたぶんのずれ

        outline = self.corner_offsets * (1 + 2 * self.context) * s  # 四角形 + 外側の手がかり
        box_center = np.array([self.tpl_w / 2.0 + self._offset[0], self.tpl_h / 2.0 + self._offset[1]])
        templates, masks = [], []
        for angle in self.angles:
            matrix = cv2.getRotationMatrix2D((cx, cy), angle, 1.0)
            rotated = cv2.warpAffine(gray, matrix, (gray.shape[1], gray.shape[0]),
                                     flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE)
            templates.append(rotated[y0:y0 + self.tpl_h, x0:x0 + self.tpl_w].copy())
            a = math.radians(angle)
            rot = np.array([[math.cos(a), math.sin(a)], [-math.sin(a), math.cos(a)]])
            mask = np.zeros((self.tpl_h, self.tpl_w), np.uint8)  # 四角形の外の背景は照合に使わない
            cv2.fillConvexPoly(mask, np.round(outline @ rot.T + box_center).astype(np.int32), 255)
            masks.append(mask)
        return templates, masks

    def track(self, frame):
        """現在フレームからバッグの位置と傾きを探す。Pose を返す(探せなければ None)。"""
        w, h = self.size
        s = self.scale
        grow = 1.0 + 0.75 * self._lost  # 見失っている間は探す範囲を広げる
        half_w = self.tpl_w / (2 * s) + self.search_margin * w * grow
        half_h = self.tpl_h / (2 * s) + self.search_margin * h * grow
        cx, cy = self._center
        wx0, wx1, wy0, wy1 = int(cx - half_w), int(cx + half_w), int(cy - half_h), int(cy + half_h)

        crop = crop_padded(frame, wx0, wy0, wx1, wy1)
        crop_w, crop_h = wx1 - wx0, wy1 - wy0
        small_w, small_h = int(round(crop_w * s)), int(round(crop_h * s))
        if small_w < self.tpl_w or small_h < self.tpl_h:
            self._lost += 1
            return None
        if s < 1.0:
            crop = cv2.resize(crop, (small_w, small_h), interpolation=cv2.INTER_AREA)
        gray = self._prepare(crop)

        best = None  # (一致度, 位置, 角度)
        for angle, template, mask in zip(self.angles, self._templates, self._masks):
            result = cv2.matchTemplate(gray, template, cv2.TM_CCOEFF_NORMED, mask=mask)
            result = np.nan_to_num(result, nan=-1.0, posinf=-1.0, neginf=-1.0)
            _, score, _, location = cv2.minMaxLoc(result)
            if best is None or score > best[0]:
                best = (float(score), location, angle)

        score, (lx, ly), angle = best
        center = (wx0 + (lx + self.tpl_w / 2.0 + self._offset[0]) / (small_w / crop_w),
                  wy0 + (ly + self.tpl_h / 2.0 + self._offset[1]) / (small_h / crop_h))
        if score >= TRACK_MIN_SCORE:
            self._center, self._lost = center, 0
        else:
            self._lost += 1

        # 選択した四角形を、見つけた位置・傾きに合わせて動かしたものが輪郭
        a = math.radians(angle)
        cos_a, sin_a = math.cos(a), math.sin(a)
        contour = np.array(
            [[round(center[0] + cos_a * px + sin_a * py), round(center[1] - sin_a * px + cos_a * py)]
             for px, py in self.corner_offsets], np.int32).reshape(-1, 1, 2)
        return Pose(self.base_angle + angle, contour, score)  # 傾きは真っすぐ(画像の縦)からの角度


# ---------------------------------------------------------------- 引数・入力


def parse_args():
    p = argparse.ArgumentParser(
        description="Make YOLO segmentation training data for the punching bag. "
                    "The area you select on each video is treated as the bag."
    )
    p.add_argument("sources", nargs="+",
                   help="Video paths (bag.mp4) and/or a camera index (0). Each is selected separately.")
    p.add_argument("--out", type=Path, default=DEFAULT_OUT, help="Dataset folder (default: dataset/)")
    p.add_argument("--fresh", action="store_true",
                   help="Delete the images and labels already in --out before collecting.")
    p.add_argument("--start-frame", type=int, default=0,
                   help="Frame number used for selecting the bag, and where collection starts (default: 0)")
    p.add_argument("--every", type=int, default=3, help="Save every N-th frame (default: 3)")
    p.add_argument("--max-images", type=int, default=600,
                   help="Stop after saving this many images from each video (default: 600)")
    p.add_argument("--save-width", type=int, default=960,
                   help="Shrink saved images to this width; keeps training fast (default: 960)")
    p.add_argument("--max-tilt", type=float, default=15.0,
                   help="Tilt the selected area up to this many degrees when searching (default: 15)")
    p.add_argument("--tilt-step", type=float, default=1.5,
                   help="Angle step in degrees; smaller = finer but slower (default: 1.5)")
    p.add_argument("--context", type=float, default=0.15,
                   help="How much of the surroundings of the selected area is also used for "
                        "matching, as a ratio of its size (default: 0.15)")
    p.add_argument("--min-match", type=float, default=0.5,
                   help="Only save frames whose match score (0-1) with the selected area is at "
                        "least this (default: 0.5)")
    p.add_argument("--search-margin", type=float, default=0.8,
                   help="How far around the previous position to search, as a ratio of the bag "
                        "size. Raise it for fast punches (default: 0.8)")
    p.add_argument("--preview-frames", type=int, default=150,
                   help="Before collecting, follow the bag through this many frames and show the "
                        "most tilted one for checking (default: 150, 0 = off)")
    p.add_argument("--no-light-normalize", action="store_true",
                   help="Match raw brightness instead of normalizing shadows (not recommended)")
    p.add_argument("--select", choices=["quad", "rect"], default="quad",
                   help="quad: click the 4 corners (a slanted rectangle, so a tilted bag can be "
                        "selected without the background). rect: drag a rectangle (default: quad)")
    p.add_argument("--no-display", action="store_true", help="Do not show any windows")
    return p.parse_args()


def open_source(source, start_frame):
    """動画またはカメラを開き、開始位置のフレームを返す。(cap, フレーム, カメラか)。"""
    is_camera = source.isdigit()
    cap = cv2.VideoCapture(int(source) if is_camera else source)
    if not cap.isOpened():
        print(f"Could not open: {source}")
        return None, None, is_camera
    if start_frame > 0 and not is_camera:
        cap.set(cv2.CAP_PROP_POS_FRAMES, start_frame)
    ok, first_frame = cap.read()
    if not ok:
        cap.release()
        print(f"Could not read the frame {start_frame} of: {source}")
        return None, None, is_camera
    return cap, first_frame, is_camera


def make_tracker(frame, region, args):
    return TemplateTracker(frame, region, args.max_tilt, args.tilt_step, args.context,
                           args.search_margin, normalize=not args.no_light_normalize)


# ---------------------------------------------------------------- 確認画面


@dataclass
class Check:
    """収集を始める前の確認の結果。"""

    ok: bool
    reason: str
    frame: np.ndarray  # 確認画面に出すフレーム
    contour: object  # 輪郭 or None
    info: str = ""


def check_start(frame, region, args, source, is_camera):
    """
    動画の先頭の数秒(--preview-frames)を実際に追跡し、いちばん傾きの大きかったフレームを見せる。
    先頭の多くが一致しない(背景を含めて囲んだ等)ときは NG にする。
    """
    try:
        tracker = make_tracker(frame, region, args)
    except SystemExit as error:
        return Check(False, str(error), frame, None)

    def stress(pose):  # 囲んだ向きからの傾きを、探索範囲で割った値(追跡に厳しいほど大きい)
        return abs(pose.angle_deg - tracker.base_angle) / max(args.max_tilt, 1e-6)

    first = tracker.track(frame)
    if first is None:
        return Check(False, "Could not match the selected area.", frame, None)
    scores = [first.score]
    best_frame, best, best_stress = frame, first, stress(first)

    n_frames = 0
    if not is_camera and args.preview_frames > 0:
        cap = cv2.VideoCapture(source)
        cap.set(cv2.CAP_PROP_POS_FRAMES, args.start_frame + 1)
        for n_frames in range(1, args.preview_frames + 1):
            ok, current = cap.read()
            if not ok:
                break
            pose = tracker.track(current)
            scores.append(pose.score if pose is not None else 0.0)
            if pose is not None and pose.score >= args.min_match and stress(pose) > best_stress:
                best_frame, best, best_stress = current, pose, stress(pose)
        cap.release()

    ratio = float(np.mean([v >= args.min_match for v in scores]))
    info = (f"Frames matched well in the first {n_frames} frames: {ratio:.0%}"
            if n_frames else "Showing the selected frame.")
    if ratio < 0.7:
        return Check(False, "Few of the first frames match the selected area well. "
                            "Select only the bag, or lower --min-match.", best_frame, best.contour, info)
    return Check(True, "", best_frame, best.contour, info)


def choose_region(first_frame, args, display, source, is_camera):
    """バッグを囲み、追跡できることを確認する。囲んだ範囲か None(この動画を飛ばす)を返す。"""
    while True:
        title = "Select the bag (Q = cancel)"
        if args.select == "quad":
            region = select_quad_or_none(first_frame, window_name=title, target="the punching bag")
        else:
            region = select_rect_or_none(first_frame, window_name=title)
        if region is None:
            print("Nothing selected. Skipping this source.")
            return None
        region = quad_from_region(region)

        if display and not is_camera and args.preview_frames > 0:
            print(f"Checking the first {args.preview_frames} frames (this may take a moment)...")
        check = check_start(first_frame, region, args, source, is_camera)
        if not display:
            if check.ok:
                return region
            print(f"{check.reason} Skipping this source (select again without --no-display).")
            return None

        view, scale = fit_to_screen(check.frame)
        preview = view.copy()
        if check.frame is first_frame:  # 囲んだ範囲は、囲んだフレームでだけ重ねて表示する
            cv2.polylines(preview, [np.round(region * scale).astype(np.int32)], True, (0, 255, 255), 1)
        if check.contour is not None:
            cv2.drawContours(preview, [(check.contour * scale).astype(np.int32)], -1,
                             GREEN if check.ok else (0, 0, 255), 2)
        if check.ok:
            message, keys = check.info or "OK: the outline follows the bag", "ENTER = start / R = reselect / S = skip / Q = quit"
        else:
            message, keys = "WARNING: " + check.reason, "F = start anyway / R = reselect / S = skip / Q = quit"
            print(check.reason)
        cv2.putText(preview, message[:110], (10, 28), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (0, 255, 255), 2)
        cv2.putText(preview, keys, (10, 56), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (255, 255, 255), 2)
        cv2.imshow(PREVIEW_NAME, preview)
        while True:
            key = cv2.waitKey(0) & 0xFF
            if (check.ok and key in (13, 10, 32)) or (check.contour is not None and key == ord("f")):
                _close(PREVIEW_NAME)
                return region
            if key == ord("s"):
                _close(PREVIEW_NAME)
                return None
            if key == ord("q"):
                raise KeyboardInterrupt
            if key == ord("r"):
                break
        _close(PREVIEW_NAME)


# ---------------------------------------------------------------- 保存


def contour_to_label(contour, width, height):
    """輪郭を YOLO セグメンテーション形式の1行(クラス 0 + 正規化した多角形)にする。"""
    perimeter = cv2.arcLength(contour, True)
    points = cv2.approxPolyDP(contour, 0.003 * perimeter, True).reshape(-1, 2)
    if len(points) < 3:
        return None
    points = points.astype(np.float32)
    points[:, 0] = np.clip(points[:, 0] / width, 0.0, 1.0)
    points[:, 1] = np.clip(points[:, 1] / height, 0.0, 1.0)
    return "0 " + " ".join(f"{v:.6f}" for v in points.reshape(-1))


def split_for(index):
    """学習用(train)と検証用(val)に分ける。連続するフレームは似ているので、20枚ずつのまとまりで分ける。"""
    return "val" if (index // 20) % 7 == 6 else "train"


def next_index(images_dir):
    """既存のデータの続きから番号を振る(複数回に分けて収集できるように)。"""
    numbers = [int(p.stem.split("_")[-1]) for split in ("train", "val")
               for p in (images_dir / split).glob("frame_*.jpg") if p.stem.split("_")[-1].isdigit()]
    return max(numbers) + 1 if numbers else 0


def prepare_folders(out, fresh):
    if fresh:
        for kind in ("images", "labels"):
            if (out / kind).exists():
                shutil.rmtree(out / kind)
                print(f"Deleted the old {out / kind}")
    for kind in ("images", "labels"):
        for split in ("train", "val"):
            (out / kind / split).mkdir(parents=True, exist_ok=True)
    for split in ("train", "val"):  # 以前の失敗で、画像の無いラベルだけが残っていたら消す
        for label in (out / "labels" / split).glob("*.txt"):
            if not (out / "images" / split / (label.stem + ".jpg")).exists():
                label.unlink()


def ensure_validation_images(out):
    """検証用データが1枚も無いと学習できないので、足りなければ学習用から数枚コピーする。"""
    if list((out / "images" / "val").glob("*.jpg")):
        return
    train_images = sorted((out / "images" / "train").glob("*.jpg"))
    for image in train_images[:: max(1, len(train_images) // 5)][:5]:
        label = out / "labels" / "train" / (image.stem + ".txt")
        shutil.copy2(image, out / "images" / "val" / image.name)
        shutil.copy2(label, out / "labels" / "val" / label.name)
    print("Note: too few images for a separate validation set; copied a few from train.")


def save_sample(frame, contour, index, out, save_width):
    """フレームを(必要なら縮小して)保存し、ラベルを書く。保存できたら True。"""
    height, width = frame.shape[:2]
    line = contour_to_label(contour, width, height)  # 座標は 0-1 なので縮小しても同じ
    if line is None:
        return False
    image = frame
    if save_width and width > save_width:
        image = cv2.resize(frame, (save_width, int(round(height * save_width / width))),
                           interpolation=cv2.INTER_AREA)
    split, name = split_for(index), f"frame_{index:06d}"
    image_path = out / "images" / split / f"{name}.jpg"
    # 画像を先に書き、書けたときだけラベルを書く(画像の無いラベルだけが残らないように)
    try:
        write_image(image_path, image)
    except OSError as error:
        raise SystemExit(f"{error}\nCheck that the folder is writable and that there is free disk space.")
    (out / "labels" / split / f"{name}.txt").write_text(line + "\n", encoding="utf-8")
    return True


# ---------------------------------------------------------------- 収集


def collect_from_source(source, args, out, index, display):
    """1つの動画(またはカメラ)から収集する。(保存した枚数, 全体を終了するか) を返す。"""
    cap, first_frame, is_camera = open_source(source, args.start_frame)
    if cap is None:
        return 0, False

    print(f"\n=== {source} ===")
    saved = 0
    try:
        region = choose_region(first_frame, args, display, source, is_camera)
        if region is None:
            return 0, False

        tracker = make_tracker(first_frame, region, args)
        frame, frame_number = first_frame, 0
        while saved < args.max_images:
            pose = tracker.track(frame)
            good = pose is not None and pose.score >= args.min_match
            if good and frame_number % args.every == 0:
                if save_sample(frame, pose.contour, index + saved, out, args.save_width):
                    saved += 1

            if display:
                shown, scale = fit_to_screen(frame)
                shown = shown.copy()
                if pose is not None:
                    color = GREEN if good else (0, 0, 255)
                    cv2.drawContours(shown, [(pose.contour * scale).astype(np.int32)], -1, color, 2)
                    cv2.putText(shown, f"tilt {pose.angle_deg:+.1f}  match {pose.score:.2f}", (10, 56),
                                cv2.FONT_HERSHEY_SIMPLEX, 0.55, color, 2)
                cv2.putText(shown, f"Saved: {saved}  (red = not matched, not saved)", (10, 28),
                            cv2.FONT_HERSHEY_SIMPLEX, 0.55, (255, 255, 255), 2)
                cv2.imshow(WINDOW_NAME, shown)
                if cv2.waitKey(1) & 0xFF == ord("q"):
                    return saved, True

            ok, frame = cap.read()
            if not ok:
                break
            frame_number += 1
    except KeyboardInterrupt:
        return saved, True
    finally:
        cap.release()
        if display:
            cv2.destroyAllWindows()
            cv2.waitKey(1)

    print(f"Saved {saved} images from {source}")
    return saved, False


def main():
    args = parse_args()
    display = not args.no_display
    out = args.out
    prepare_folders(out, args.fresh)

    total = 0
    for source in args.sources:
        saved, stop = collect_from_source(source, args, out, next_index(out / "images"), display)
        total += saved
        if stop:
            break

    if total == 0 and next_index(out / "images") == 0:
        raise SystemExit("No images were saved. Select the bag again (only the bag, no background), "
                         "or lower --min-match / raise --search-margin.")

    ensure_validation_images(out)
    (out / "data.yaml").write_text(
        f"path: {out.resolve().as_posix()}\ntrain: images/train\nval: images/val\nnames:\n  0: punching_bag\n",
        encoding="utf-8")
    n_train = len(list((out / "images" / "train").glob("*.jpg")))
    n_val = len(list((out / "images" / "val").glob("*.jpg")))
    n_bags = sum(len(p.read_text(encoding="utf-8").splitlines()) for p in (out / "labels").glob("*/*.txt"))
    print(f"\nDataset: {n_train} train / {n_val} val images in {out}/ (data config: {out / 'data.yaml'})")
    print(f"Objects in the labels: bag {n_bags}")
    print("Next: python train_yolo.py")


if __name__ == "__main__":
    main()
