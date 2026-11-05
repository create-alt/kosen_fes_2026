"""サンドバッグの見た目(色の分布・大きさ・形)の学習・保存・読み込み。

サンドバッグは毎回同じものを使うため、一度学習した結果をファイルに保存しておき、
2回目以降はそれを読み込んで学習(領域選択)を省略する。

学習する内容
- hist        : バッグの色の分布(H, S ヒストグラム)
- area_ratio  : バッグが画面に占める面積の割合(カメラが固定なら毎回ほぼ同じ)
- elongation  : バッグの細長さ(長軸 / 短軸)

area_ratio と elongation は、追跡中に「バッグ色に近い塊」が複数見つかったとき、
本物のバッグを選ぶ手がかりに使う(bag_pose.py)。
"""

import math
from dataclasses import dataclass
from pathlib import Path
from typing import Optional

import cv2
import numpy as np

DEFAULT_MODEL_PATH = Path(__file__).with_name("bag_model.npz")
ROI_WINDOW_NAME = "Select punching bag area, then press ENTER/SPACE"

# 色ヒストグラム(H, S)の設定。学習と追跡で共通
HIST_BINS = [30, 32]
HIST_RANGES = [0, 180, 0, 256]

# 明るさ(V)も使う色ヒストグラム(H, S, V)。色味が乏しい(黒・白・灰色の)バッグでも、
# 背景との明るさの違いで区別できる。教師データ作成(collect_dataset.py)で使う。
HIST3_BINS = [24, 8, 8]
HIST3_RANGES = [0, 180, 0, 256, 0, 256]

# 領域選択の画面は、これより大きい映像を縮小して表示する(画面からはみ出さないように)
SELECT_VIEW_SIZE = (1280, 720)


@dataclass
class BagModel:
    """学習したサンドバッグの特徴。形の情報は古いモデルファイルには無いので None になりうる。"""

    hist: np.ndarray
    area_ratio: Optional[float] = None
    elongation: Optional[float] = None


def elongation_from_moments(m):
    """
    輪郭のモーメントから細長さ(長軸 / 短軸、1以上)を求める。
    正方形・円に近いほど 1、細長いほど大きくなる。
    """
    if m["m00"] == 0:
        return 1.0
    mu20 = m["mu20"] / m["m00"]
    mu02 = m["mu02"] / m["m00"]
    mu11 = m["mu11"] / m["m00"]
    spread = math.sqrt(4 * mu11 * mu11 + (mu20 - mu02) ** 2)
    major = (mu20 + mu02 + spread) / 2
    minor = (mu20 + mu02 - spread) / 2
    return math.sqrt(max(major, 1e-6) / max(minor, 1e-6))


def fit_to_screen(frame, max_size=SELECT_VIEW_SIZE):
    """画面に収まる大きさに縮小した画像と、その縮小率を返す。"""
    height, width = frame.shape[:2]
    scale = min(1.0, max_size[0] / width, max_size[1] / height)
    if scale >= 1.0:
        return frame, 1.0
    view = cv2.resize(frame, None, fx=scale, fy=scale, interpolation=cv2.INTER_AREA)
    return view, scale


def select_region_or_none(frame, window_name=ROI_WINDOW_NAME):
    """
    画面上でサンドバッグの領域をドラッグして選択する。キャンセルしたら None。
    大きい映像は縮小して表示し、選択結果は元の映像の座標に直して返す。
    """
    view, scale = fit_to_screen(frame)
    roi = cv2.selectROI(window_name, view, showCrosshair=True, fromCenter=False)
    cv2.destroyWindow(window_name)
    cv2.waitKey(1)  # ウィンドウを確実に閉じる

    x, y, width, height = roi
    if width == 0 or height == 0:
        return None
    return tuple(int(round(v / scale)) for v in (x, y, width, height))


def select_quad_or_none(
    frame, window_name="Click the 4 corners of the bag", target="the bag", existing=None
):
    """
    サンドバッグの四隅を4回クリックして、斜めを向いた四角形で領域を選択する。
    傾いたバッグでも、背景を含めずにバッグだけを囲める。

    操作: 左クリック=点を追加 / Backspace か Z=1つ戻す / R=やり直し /
          Enter か Space=決定(4点そろったとき) / Esc か Q=キャンセル
    大きい映像は縮小して表示し、結果は元の映像の座標に直した 4点 (float32, 4x2) で返す。
    キャンセルしたら None。点の順番(時計回りか、どこから始めるか)は問わない。

    target   : 何を囲むのか(画面に出す説明。例: "a boxing glove")
    existing : すでに囲んだ物体の4点のリスト(元の映像の座標)。参考として薄く表示する
    """
    view, scale = fit_to_screen(frame)
    points = []
    reference = [np.round(np.asarray(q, np.float32) * scale).astype(np.int32) for q in (existing or [])]

    def on_mouse(event, x, y, flags, param):
        if event == cv2.EVENT_LBUTTONDOWN and len(points) < 4:
            points.append((x, y))

    cv2.namedWindow(window_name, cv2.WINDOW_AUTOSIZE)
    cv2.setMouseCallback(window_name, on_mouse)
    result = None
    try:
        while True:
            canvas = view.copy()
            for q in reference:
                cv2.polylines(canvas, [q.reshape(-1, 1, 2)], True, (160, 160, 160), 1)
            if len(points) >= 2:
                # 4点そろったら閉じた四角形、途中なら折れ線で見せる
                shape = np.array(points, np.int32).reshape(-1, 1, 2)
                if len(points) == 4:
                    shape = cv2.convexHull(shape)  # クリックの順番によらず、交差しない四角形にする
                cv2.polylines(canvas, [shape], len(points) == 4, (0, 255, 255), 1)
            for i, point in enumerate(points):
                cv2.circle(canvas, point, 4, (0, 0, 255), -1)
                cv2.putText(canvas, str(i + 1), (point[0] + 6, point[1] - 6),
                            cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 0, 255), 2)
            if len(points) < 4:
                message = f"Click the 4 corners of {target} ({len(points)}/4)"
                keys = "Backspace = undo / R = reset / Q = cancel"
            else:
                message = "ENTER = decide"
                keys = "Backspace = undo / R = reset / Q = cancel"
            cv2.putText(canvas, message, (10, 28), cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 255, 255), 2)
            cv2.putText(canvas, keys, (10, 56), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (255, 255, 255), 2)
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
        cv2.destroyWindow(window_name)
        cv2.waitKey(1)  # ウィンドウを確実に閉じる
    return result


def select_bag_region(frame):
    """画面上でサンドバッグの領域をドラッグして選択する。キャンセルしたら終了する。"""
    region = select_region_or_none(frame)
    if region is None:
        raise SystemExit("No region selected. Please select the bag area.")
    return region


def _bin_lookup(n_bins, max_value):
    """画素値(0-255)から、ヒストグラムのビン番号を引くテーブル。"""
    table = np.arange(256) * n_bins // max_value
    return np.minimum(table, n_bins - 1).astype(np.intp)


_H3_LUT = _bin_lookup(HIST3_BINS[0], 180)
_S3_LUT = _bin_lookup(HIST3_BINS[1], 256)
_V3_LUT = _bin_lookup(HIST3_BINS[2], 256)


def backproject(hsv, hist):
    """
    色ヒストグラム(H,S の2次元、または H,S,V の3次元)で、バッグらしさ(0-255)を求める。

    OpenCV の calcBackProject は、Python から渡した3次元の配列を正しく扱えない
    (24x8 の8チャンネル画像と解釈され、結果が全部 0 になる)ため、3次元のときは
    ビン番号の表を引いて求める。
    """
    if hist.ndim == 3:
        values = hist[_H3_LUT[hsv[..., 0]], _S3_LUT[hsv[..., 1]], _V3_LUT[hsv[..., 2]]]
        return np.clip(values, 0, 255).astype(np.uint8)
    return cv2.calcBackProject([hsv], [0, 1], hist, HIST_RANGES, 1)


def _smooth_hist(hist, pad=2):
    """
    ヒストグラムを軽くぼかす。色相(H)は 0 と 179 がつながっている(赤が両端に
    またがる)ので、色相方向は端を巻き込んでぼかす。
    """
    wrapped = np.pad(hist, ((pad, pad), (0, 0)), mode="wrap")
    blurred = cv2.GaussianBlur(wrapped, (2 * pad + 1, 2 * pad + 1), 0)
    return blurred[pad:-pad]


def _smooth_hist3(hist):
    """3次元ヒストグラムを、各軸方向に軽くぼかす(色相は端がつながっている)。"""
    kernel = (0.25, 0.5, 0.25)
    out = hist.astype(np.float32)
    for axis, mode in enumerate(("wrap", "edge", "edge")):
        pad = [(0, 0)] * 3
        pad[axis] = (1, 1)
        padded = np.pad(out, pad, mode=mode)
        n = out.shape[axis]

        def piece(start):
            return padded[tuple(
                slice(start, start + n) if i == axis else slice(None)
                for i in range(3)
            )]

        out = kernel[0] * piece(0) + kernel[1] * piece(1) + kernel[2] * piece(2)
    return out


def learn_model_from_region(
    frame, region, analysis_width=320, margin=0.6, use_value=True
):
    """
    選択した領域の中身を、そのまま「サンドバッグ」として学習する。

    GrabCut のように「領域の中から、さらにバッグを探す」ことはしない。
    領域の内側(外周 10% を除く)をバッグ、領域の外側の周辺を背景とみなして、
    「バッグに多く、背景に少ない色」を学習する。そのため、バッグの一部(クッション部分
    だけなど)しか映っていなくても、選択した範囲がバッグとして扱われる。

    use_value=True  : 色相・彩度・明るさ(H,S,V)で学習する(既定。黒・白・灰色のバッグにも強い)
    use_value=False : 色相・彩度(H,S)だけで学習する(明るさが変わる環境向け)
    """
    frame_w = frame.shape[1]
    scale = min(1.0, analysis_width / frame_w)
    small = cv2.resize(frame, None, fx=scale, fy=scale, interpolation=cv2.INTER_AREA)
    small_h, small_w = small.shape[:2]

    x, y, width, height = (int(round(v * scale)) for v in region)
    x, y = max(0, min(x, small_w - 1)), max(0, min(y, small_h - 1))
    width, height = min(width, small_w - x), min(height, small_h - y)
    if width < 6 or height < 6:
        raise SystemExit("Selected area is too small. Select the bag again.")

    margin_x, margin_y = int(width * margin), int(height * margin)
    x0, y0 = max(0, x - margin_x), max(0, y - margin_y)
    x1, y1 = min(small_w, x + width + margin_x), min(small_h, y + height + margin_y)
    hsv = cv2.cvtColor(small[y0:y1, x0:x1], cv2.COLOR_BGR2HSV)

    rx, ry = x - x0, y - y0  # 切り出し内での、選択領域の位置
    inner_x, inner_y = int(width * 0.1), int(height * 0.1)

    bag_mask = np.zeros(hsv.shape[:2], np.uint8)
    bag_mask[
        ry + inner_y : ry + height - inner_y, rx + inner_x : rx + width - inner_x
    ] = 255
    background_mask = np.full(hsv.shape[:2], 255, np.uint8)
    background_mask[ry : ry + height, rx : rx + width] = 0

    if use_value:
        channels, bins, ranges = [0, 1, 2], HIST3_BINS, HIST3_RANGES
    else:
        channels, bins, ranges = [0, 1], HIST_BINS, HIST_RANGES
    bag_hist = cv2.calcHist([hsv], channels, bag_mask, bins, ranges)
    bg_hist = cv2.calcHist([hsv], channels, background_mask, bins, ranges)

    if use_value:
        bag_hist, bg_hist = _smooth_hist3(bag_hist), _smooth_hist3(bg_hist)
    else:
        bag_hist, bg_hist = _smooth_hist(bag_hist), _smooth_hist(bg_hist)

    # 「背景にも多い色」を弱めるため、バッグ色の割合(0-1)をヒストグラムにする
    bag_hist /= bag_hist.sum() + 1e-6
    bg_hist /= bg_hist.sum() + 1e-6
    ratio = bag_hist / (bag_hist + bg_hist + 1e-6)
    hist = (ratio * 255).astype(np.float32)

    # 大きさと形は、選択した領域そのものから求める
    rect = np.array(
        [[0, 0], [width, 0], [width, height], [0, height]], np.float32
    ).reshape(-1, 1, 2)
    area_ratio = float(width * height / (small_w * small_h))
    elongation = float(elongation_from_moments(cv2.moments(rect)))
    return BagModel(hist=hist, area_ratio=area_ratio, elongation=elongation)


def learn_bag_model(frame, region, analysis_width=320, margin=0.6):
    """
    選択領域から GrabCut でバッグ部分を切り出し、バッグの色・大きさ・形を学習する。
    追跡時と同じ縮小サイズで学習する(解像度が違うと色の見え方が少し変わるため)。
    """
    frame_w = frame.shape[1]
    scale = min(1.0, analysis_width / frame_w)
    small = cv2.resize(
        frame, None, fx=scale, fy=scale, interpolation=cv2.INTER_AREA
    )
    small_h, small_w = small.shape[:2]

    x, y, width, height = (int(round(v * scale)) for v in region)
    if width < 5 or height < 5:
        raise SystemExit("Selected area is too small. Select the bag again.")

    margin_x, margin_y = int(width * margin), int(height * margin)
    x0, y0 = max(0, x - margin_x), max(0, y - margin_y)
    x1, y1 = min(small_w, x + width + margin_x), min(small_h, y + height + margin_y)
    crop = small[y0:y1, x0:x1]

    rect = (
        x - x0,
        y - y0,
        min(width, crop.shape[1] - (x - x0)),
        min(height, crop.shape[0] - (y - y0)),
    )

    mask = np.zeros(crop.shape[:2], np.uint8)
    bgd_model = np.zeros((1, 65), np.float64)
    fgd_model = np.zeros((1, 65), np.float64)
    cv2.grabCut(crop, mask, rect, bgd_model, fgd_model, 5, cv2.GC_INIT_WITH_RECT)

    bag_mask = np.where(
        (mask == cv2.GC_FGD) | (mask == cv2.GC_PR_FGD), 255, 0
    ).astype(np.uint8)
    if cv2.countNonZero(bag_mask) < 100:
        raise SystemExit(
            "Could not find the bag in the selected area. "
            "Select the bag more tightly and try again."
        )

    # --- 色の分布 ---
    hsv = cv2.cvtColor(crop, cv2.COLOR_BGR2HSV)
    bag_hist = cv2.calcHist([hsv], [0, 1], bag_mask, HIST_BINS, HIST_RANGES)
    bg_hist = cv2.calcHist(
        [hsv], [0, 1], cv2.bitwise_not(bag_mask), HIST_BINS, HIST_RANGES
    )

    # 照明やカメラノイズによる色のずれ(保存モデルを別の日に使う場合など)に
    # 強くするため、隣のビンにも広げてなめらかにする
    bag_hist = _smooth_hist(bag_hist)
    bg_hist = _smooth_hist(bg_hist)

    # 「背景にも多い色」を弱めるため、バッグ色の割合(0-1)をヒストグラムにする
    bag_hist /= bag_hist.sum() + 1e-6
    bg_hist /= bg_hist.sum() + 1e-6
    ratio = bag_hist / (bag_hist + bg_hist + 1e-6)
    hist = (ratio * 255).astype(np.float32)

    # --- 大きさと形(GrabCut で切り出した最大の塊から求める) ---
    area_ratio, elongation = None, None
    contours, _ = cv2.findContours(
        bag_mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE
    )
    if contours:
        bag_contour = max(contours, key=cv2.contourArea)
        moments = cv2.moments(bag_contour)
        if moments["m00"] > 0:
            area_ratio = float(moments["m00"] / (small_w * small_h))
            elongation = float(elongation_from_moments(moments))

    return BagModel(hist=hist, area_ratio=area_ratio, elongation=elongation)


def save_model(path, model):
    """学習結果をファイルに保存する。"""
    path = Path(path)
    nan = float("nan")
    with open(path, "wb") as f:
        np.savez(
            f,
            hist=model.hist,
            area_ratio=nan if model.area_ratio is None else model.area_ratio,
            elongation=nan if model.elongation is None else model.elongation,
        )
    print(f"Saved bag model: {path}")


def _optional_float(data, key):
    """npz から値を読む。無い・NaN の場合は None。"""
    if key not in data.files:
        return None
    value = float(data[key])
    return value if math.isfinite(value) else None


def load_model(path):
    """保存済みの学習結果を読み込む。無い・壊れている場合は None。"""
    path = Path(path)
    if not path.exists():
        return None
    try:
        with np.load(path) as data:
            hist = data["hist"].astype(np.float32)
            area_ratio = _optional_float(data, "area_ratio")
            elongation = _optional_float(data, "elongation")
    except Exception as error:  # 壊れたファイルなど
        print(f"Could not read bag model ({error}). It will be learned again.")
        return None

    valid_shapes = (tuple(HIST_BINS), tuple(HIST3_BINS))
    if hist.shape not in valid_shapes:
        print("Saved bag model has an unexpected shape. It will be learned again.")
        return None
    return BagModel(hist=hist, area_ratio=area_ratio, elongation=elongation)


def get_bag_model(model_path, force_learn, first_frame, analysis_width):
    """
    バッグの学習結果(BagModel)を返す。

    - force_learn が True        : 領域を選択して学習し、保存する
    - 保存済みモデルがある        : それを読み込む(領域選択は不要)
    - 保存済みモデルが無い(初回): 領域を選択して学習し、保存する
    """
    if not force_learn:
        model = load_model(model_path)
        if model is not None:
            print(f"Loaded bag model: {model_path}")
            if model.area_ratio is None or model.elongation is None:
                print(
                    "This model has no shape information (saved by an older "
                    "version). Run with --learn once to enable shape checking."
                )
            return model
        print("No saved bag model found. Learning the bag from this run.")

    region = select_bag_region(first_frame)
    model = learn_bag_model(first_frame, region, analysis_width)
    save_model(model_path, model)
    return model
