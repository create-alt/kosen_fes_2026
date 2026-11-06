"""画面のエフェクト。Python の出力画面(realtime.py / main.py)だけに描く。Unity へ送る値は変えない。

    fx = Effects()
    frame = fx.render(frame, pipeline.bag, pipeline.person, result, counts=judge.punch_counts)

デザインの方針: 文字の擬音・おもちゃっぽい小物は使わず、光と色の演出だけで見せる(映画・スポーツ中継風)。
色は 2 色だけ(左手 = 青白い光、右手 = 琥珀色)。パンチを続けると「エネルギー」が溜まり、光の色が寒色から暖色へ移る。

    背景   : 映像の色・明るさは元のまま。ごく淡い光の粒が漂う。
    人     : 輪郭に沿った細い光(リムライト)。手の軌跡は細い光の帯。
    動き   : 動くと体の残像(シルエット)が残り、足元に土ぼこり。
    パンチ : 衝撃点の光、素材の火花の爆発と十字の閃光、細い火花、バッグの縁が光る。
    素材   : assets/fx/ の画像は Kenney「Particle Pack」(CC0。クレジット不要・商用可)。無ければ線と点で代わりに描く。
    表示   : 右上にコンボ数(2 以上のとき)、左下にパンチ数(左右別)。細い線と数字だけ。
"""

import math
import random
import time
from collections import deque
from pathlib import Path

import cv2
import numpy as np

try:
    from PIL import Image, ImageDraw, ImageFont
except ImportError:  # Pillow が無ければ、数字は OpenCV のフォントで描く
    Image = ImageDraw = ImageFont = None

NOSE, L_EYE, R_EYE, L_EAR, R_EAR = 0, 1, 2, 3, 4
L_SHOULDER, R_SHOULDER, L_ELBOW, R_ELBOW, L_WRIST, R_WRIST = 5, 6, 7, 8, 9, 10
L_HIP, R_HIP, L_KNEE, R_KNEE, L_ANKLE, R_ANKLE = 11, 12, 13, 14, 15, 16
HEAD_POINTS = (NOSE, L_EYE, R_EYE, L_EAR, R_EAR)
BODY_LINES = [
    (L_SHOULDER, R_SHOULDER), (L_SHOULDER, L_ELBOW), (L_ELBOW, L_WRIST), (R_SHOULDER, R_ELBOW), (R_ELBOW, R_WRIST),
    (L_SHOULDER, L_HIP), (R_SHOULDER, R_HIP), (L_HIP, R_HIP),
    (L_HIP, L_KNEE), (L_KNEE, L_ANKLE), (R_HIP, R_KNEE), (R_KNEE, R_ANKLE),
]
ELBOW = {"L": L_ELBOW, "R": R_ELBOW}

COOL = (255, 205, 90)  # BGR: ice cyan。白寄りで映像になじませる
WARM = (20, 95, 255)   # BGR: deep amber。赤みを抑えた映画的な暖色
WHITE = (250, 250, 250)
SIDE = {"L": COOL, "R": WARM}

FONT_CANDIDATES = [
    "C:/Windows/Fonts/bahnschrift.ttf",  # DIN 風(スポーツ中継の数字に合う)
    "C:/Windows/Fonts/segoeuib.ttf",
    "C:/Windows/Fonts/arialbd.ttf",
    "/System/Library/Fonts/Helvetica.ttc",
    "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
]


def mix(a, b, k):
    return tuple(int(x + (y - x) * k) for x, y in zip(a, b))


def scaled(color, k):
    return tuple(int(max(0, min(255, c * k))) for c in color)


def ease_out(f):
    return 1 - (1 - f) ** 3


ASSET_DIR = Path(__file__).resolve().parent / "assets" / "fx"  # Kenney "Particle Pack"(CC0)から選んだ画像


def load_sprites(folder=ASSET_DIR):
    """黒背景の光の画像を {名前: 明るさ 0〜1 の float32} で読む。無ければ空(その演出は線と点で代わりに描く)。"""
    sprites = {}
    for path in sorted(Path(folder).glob("*.png")):
        img = cv2.imread(str(path), cv2.IMREAD_GRAYSCALE)
        if img is not None:
            sprites[path.stem] = img.astype(np.float32) / 255
    return sprites


_GLOW_CACHE = {}


def _glow_sprite(radius):
    """中心 1、外へなめらかに 0 になる光の形 (2r+1, 2r+1)。"""
    if radius not in _GLOW_CACHE:
        if len(_GLOW_CACHE) > 64:
            _GLOW_CACHE.clear()
        yy, xx = np.mgrid[-radius:radius + 1, -radius:radius + 1].astype(np.float32)
        d2 = (xx * xx + yy * yy) / float(radius * radius)
        _GLOW_CACHE[radius] = np.exp(-d2 * 4.0) * 0.85 + np.exp(-d2 * 25.0) * 0.15
    return _GLOW_CACHE[radius]


class Particle:
    __slots__ = ("x", "y", "vx", "vy", "age", "life", "kind", "color", "size", "drag", "grav")

    def __init__(self, x, y, vx, vy, life, kind, color, size, drag=0.0, grav=0.0):
        self.x, self.y, self.vx, self.vy = x, y, vx, vy
        self.age, self.life, self.kind, self.color, self.size = 0.0, life, kind, color, size
        self.drag, self.grav = drag, grav


class Effects:
    """毎フレーム render() を呼ぶと、そのフレームにエフェクトを重ねたものを返す(状態は中で持つ)。"""

    MAX_PARTICLES = 110
    COMBO_GAP = 1.2  # この秒数以内に次のパンチが来たらコンボが続く
    FX_STEP = 0.05  # 演出の時計が 1 フレームで進む最大の秒数(10fps 前後でも、0.2 秒の演出が約 4 フレーム見える)

    def __init__(self, seed=None):
        self.rng = random.Random(seed)
        self.font_path = next((p for p in FONT_CANDIDATES if Path(p).exists()), None) if Image else None
        self._fonts, self._sprites = {}, {}
        self.size = None
        self.particles, self.rings, self.flares, self.streaks, self.echoes = [], [], [], [], []
        self.images = load_sprites()
        self.sprites = []  # 画面に出ている素材の画像(パンチの光・足元の土ぼこり)
        self._echo_requests = []
        self.trails = {"L": deque(), "R": deque()}
        self.body_hist = deque()
        self.last_t = None
        self.torso = 120.0
        self.speed = 0.0  # 体の横の速さ(体幹の長さ/秒)
        self.energy = 0.0  # パンチで溜まり、時間で減る(0〜1)。色の温度・光の強さに使う
        self.combo, self.last_punch_t, self.combo_start = 0, -1e9, -1e9
        self.counts = {"L": 0, "R": 0}
        self.flash = self.bag_glow = 0.0
        self.impact_pulse = 0.0  # 画面周辺を一瞬だけ締める、低彩度の衝撃パルス
        self._vignette = None  # 解像度変更時だけ生成。毎フレームの np.mgrid を避ける
        self.next_step = self.next_echo = 0.0
        self.foot = 0
        self.dash_ready = self.duck_ready = True
        self.prev_hit = {"L": False, "R": False}
        self.fx_clock = 0.0  # 演出の時計(1 フレームで最大 FX_STEP 秒しか進めない。fps が低くても演出が数フレーム見える)
        # リアルタイム優先。エフェクト単体の予算は約8ms、超えたら自動で品質を落とす。
        self.target_fx_ms = 8.0
        self.fx_ms_ema = 0.0
        self.quality = 2  # 2=通常、1=軽量、0=最低遅延
        self._frame_id = 0
        self._cached_motes = None
        self._cached_rim = None
        self._light_buffer = None
        self._light_buffer = None

    # ------------------------------------------------------------------ 外から呼ぶ

    def render(self, frame, bag, person, result, counts=None):
        """本番向け最低負荷版。全画面ぼかし、人物マスク、残像、Bloom、背景粒を使わない。"""
        t = result.time
        dt = 1 / 30 if self.last_t is None or t <= self.last_t else min(0.1, t - self.last_t)
        self.last_t = t
        self.fx_clock += min(dt, self.FX_STEP)
        ft, fdt = self.fx_clock, min(dt, self.FX_STEP)  # 演出(光・筋・火花)の時刻と経過時間
        h, w = frame.shape[:2]
        if self.size != (w, h):
            self._init_size(w, h)
        if counts is not None:
            self.counts = dict(counts)

        kp = self._keypoints(person)
        self.torso = self._torso(kp, person) or self.torso
        self.energy = max(0.0, self.energy - dt * 0.24)
        self._update_motion(t, kp, person, result)
        self._update_hands(t, result)
        self._events(t, kp, bag, result, counts is None)

        # 残像と画像スプライトは生成されても即破棄する。
        self._echo_requests.clear()
        self.echoes.clear()
        self.sprites.clear()

        active = bool(self.particles or self.rings or self.flares or self.streaks or
                      any(self.trails.values()) or (bag is not None and self.bag_glow > 0.02))
        if active:
            if self._light_buffer is None or self._light_buffer.shape != frame.shape:
                self._light_buffer = np.zeros_like(frame)
            else:
                self._light_buffer.fill(0)
            light = self._light_buffer

            # 細線と少数粒子だけ。GaussianBlur、resize、float画像合成は無し。
            self._draw_trails(light, t)
            self._draw_rings(light, ft)
            self._draw_flares(light, ft)
            self._step_particles(light, fdt)
            if bag is not None and self.bag_glow > 0.03:
                smooth = cv2.approxPolyDP(cv2.convexHull(bag.contour),
                                          0.015 * cv2.arcLength(bag.contour, True), True)
                cv2.drawContours(light, [smooth], -1, scaled(WHITE, 0.32 * self.bag_glow),
                                 1, cv2.LINE_AA)
            cv2.add(frame, light, dst=frame)
        if self.streaks:  # パンチの光の筋は、足し算ではなく色で直接描く(白いバッグ・白い壁の前でも見える)
            self._draw_streaks(frame, ft)

        # 全画面フラッシュと周辺減光も省略。等速性を優先する。
        self.flash = 0.0
        self.impact_pulse = 0.0
        self.bag_glow *= math.exp(-dt * 12)
        self.rings = [x for x in self.rings if ft - x["t0"] < x["life"]]
        self.flares = [x for x in self.flares if ft - x["t0"] < x["life"]]
        self.streaks = [x for x in self.streaks if ft - x["t0"] < x["life"]]

        self._draw_hud(frame, t, result)
        return frame

    # ------------------------------------------------------------------ 準備・状態の更新

    def _init_size(self, w, h):
        self.size = (w, h)
        self.unit = max(w, h) / 640.0
        self.motes = [self._new_mote(w, h, self.rng.uniform(0, h)) for _ in range(10)]
        # impact grade 用マスクを一度だけ作る。float32 1ch、0〜1。
        yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
        dx = (xx - w * 0.5) / max(1.0, w * 0.72)
        dy = (yy - h * 0.5) / max(1.0, h * 0.72)
        self._vignette = np.clip(dx * dx + dy * dy, 0, 1)
        self._cached_motes = None
        self._cached_rim = None
        self._light_buffer = None

    def _new_mote(self, w, h, y=None):
        return {"x": self.rng.uniform(0, w), "y": h + 10 if y is None else y, "r": self.rng.uniform(0.5, 1.4) * self.unit,
                "vy": -self.rng.uniform(4, 14) * self.unit, "phase": self.rng.uniform(0, 6.28)}

    @staticmethod
    def _keypoints(person):
        if person is None:
            return {}
        return {i: p for i in range(len(person.keypoints)) if (p := person.point(i)) is not None}

    @staticmethod
    def _mid(kp, a, b):
        pts = [kp[i] for i in (a, b) if i in kp]
        return (sum(p[0] for p in pts) / len(pts), sum(p[1] for p in pts) / len(pts)) if pts else None

    def _head(self, kp):
        pts = [kp[i] for i in HEAD_POINTS if i in kp]
        return (sum(p[0] for p in pts) / len(pts), sum(p[1] for p in pts) / len(pts)) if pts else None

    def _body(self, kp):
        s, hp = self._mid(kp, L_SHOULDER, R_SHOULDER), self._mid(kp, L_HIP, R_HIP)
        if s and hp:
            return ((s[0] + hp[0]) / 2, (s[1] + hp[1]) / 2)
        return s or hp

    def _torso(self, kp, person):
        s, hp = self._mid(kp, L_SHOULDER, R_SHOULDER), self._mid(kp, L_HIP, R_HIP)
        if s and hp:
            d = math.hypot(s[0] - hp[0], s[1] - hp[1])
            if d > 10:
                return 0.8 * self.torso + 0.2 * d
        if person is not None:
            return 0.9 * self.torso + 0.1 * max(30.0, (person.box[3] - person.box[1]) / 3)
        return None

    def _accent(self):
        """いまの光の色(エネルギーが溜まるほど暖色)。"""
        return mix(COOL, WARM, min(1.0, self.energy * 1.2))

    def _update_motion(self, t, kp, person, result):
        """歩く・踏み込む・頭を下げる動きから、残像と床の波紋を出す。"""
        T = self.torso
        center = self._body(kp) or self._head(kp)
        if center is not None:
            self.body_hist.append((t, center[0], center[1]))
        while self.body_hist and t - self.body_hist[0][0] > 0.25:
            self.body_hist.popleft()
        if len(self.body_hist) >= 2 and self.body_hist[-1][0] - self.body_hist[0][0] > 0.05:
            (t0, x0, _), (t1, x1, _) = self.body_hist[0], self.body_hist[-1]
            self.speed = 0.6 * self.speed + 0.4 * ((x1 - x0) / (t1 - t0) / T)
        else:
            self.speed *= 0.8
        moving = abs(self.speed) > 0.8 and center is not None

        if moving and t >= self.next_step:  # 足元に床の波紋(左右の足を交互に)
            self.next_step = t + max(0.16, 0.3 - abs(self.speed) * 0.04)
            self.foot ^= 1
            foot = kp.get(L_ANKLE if self.foot else R_ANKLE) or kp.get(R_ANKLE if self.foot else L_ANKLE)
            if foot is None and person is not None:
                foot = (center[0] + (T * 0.2 if self.foot else -T * 0.2), person.box[3])
            if foot is not None:
                smoke = [n for n in ("smoke_04", "smoke_05") if n in self.images]
                if smoke:  # 素材の土ぼこりが、足元でふわっと広がって消える
                    self.sprites.append({"t0": t, "name": self.rng.choice(smoke), "x": foot[0], "y": foot[1] - T * 0.1,
                                         "vy": -T * 0.25, "size": (T * 0.45, T * 0.9), "angle": self.rng.uniform(0, 360),
                                         "spin": self.rng.uniform(-40, 40), "life": 0.7, "color": (215, 220, 225),
                                         "opacity": 0.32, "additive": False})
                else:
                    self.rings.append({"t0": self.fx_clock, "x": foot[0], "y": foot[1], "r": T * 0.9, "life": 0.6, "aspect": 0.22,
                                       "angle": 0.0, "color": scaled(self._accent(), 0.7), "thick": 1})
                for _ in range(3):
                    self._add(Particle(foot[0] + self.rng.uniform(-0.3, 0.3) * T, foot[1], self.rng.uniform(-10, 10),
                                       -self.rng.uniform(15, 40) * self.unit, self.rng.uniform(0.6, 1.0), "mote",
                                       self._accent(), 1.6 * self.unit, drag=1.0))
        if moving and t >= self.next_echo:
            self.next_echo = t + 0.07
            self._echo_requests.append((self._accent(), 0.4, 0.35))

        if abs(self.speed) > 2.4 and self.dash_ready and center is not None:  # 速い踏み込み: 強めの残像
            self.dash_ready = False
            self._echo_requests.append((WHITE, 0.5, 0.6))
        elif abs(self.speed) < 1.2:
            self.dash_ready = True

        v = result.vertical
        if v is not None and v < -0.35 and self.duck_ready and kp:  # 頭を下げた(かわした): 残像
            self.duck_ready = False
            self._echo_requests.append((COOL, 0.5, 0.55))
        elif v is None or v > -0.15:
            self.duck_ready = True

    def _update_hands(self, t, result):
        for side in ("L", "R"):
            trail = self.trails[side]
            p = result.hands_lr.get(side)
            if p is not None:
                trail.append((t, p[0], p[1]))
            while trail and t - trail[0][0] > 0.14:
                trail.popleft()
            hit = bool(result.hit_sides.get(side))
            if hit and not self.prev_hit[side] and p is not None:  # 触れた瞬間: 小さく光る
                self.flares.append({"t0": self.fx_clock, "x": p[0], "y": p[1], "r": self.torso * 0.25, "life": 0.12,
                                    "color": SIDE[side]})
            self.prev_hit[side] = hit

    def _events(self, t, kp, bag, result, count_here):
        """パンチした瞬間の演出。"""
        T, u = self.torso, self.unit
        if self.energy > 0.55 and kp and self.rng.random() < self.energy * 0.6:  # エネルギーが高い: 体から火の粉
            pts = [kp[i] for i in (L_SHOULDER, R_SHOULDER, L_ELBOW, R_ELBOW, L_HIP, R_HIP) if i in kp]
            if pts:
                px, py = self.rng.choice(pts)
                self._add(Particle(px + self.rng.uniform(-0.3, 0.3) * T, py, self.rng.uniform(-12, 12),
                                   -self.rng.uniform(30, 70) * u, self.rng.uniform(0.8, 1.4), "ember", WARM,
                                   self.rng.uniform(1.2, 2.2) * u, drag=0.4, grav=-10 * u))

        for side in result.punch_sides:
            if count_here:
                self.counts[side] = self.counts.get(side, 0) + 1
            hand = result.hands_lr.get(side)
            target = bag.center if bag is not None else None
            if hand is None:
                hand = target or self._body(kp) or (self.size[0] / 2, self.size[1] / 2)
            x, y = hand
            if target is not None:
                x, y = x + (target[0] - x) * 0.25, y + (target[1] - y) * 0.1
            # パンチの向き(肘 → 手首、無ければ手 → バッグ)
            elbow = kp.get(ELBOW[side])
            if elbow is not None and result.hands_lr.get(side) is not None:
                dx, dy = hand[0] - elbow[0], hand[1] - elbow[1]
            elif target is not None:
                dx, dy = target[0] - hand[0], target[1] - hand[1]
            else:
                dx, dy = 1.0, 0.0
            n = math.hypot(dx, dy) or 1.0
            dx, dy = dx / n, dy / n
            ang = math.atan2(dy, dx)

            if t - self.last_punch_t > self.COMBO_GAP:
                self.combo, self.combo_start = 0, t
            self.combo += 1
            self.last_punch_t = t
            self.energy = min(1.0, self.energy + 0.14)
            color = SIDE[side]
            k = 0.7 + 0.3 * self.energy

            self.flares.append({"t0": self.fx_clock, "x": x, "y": y, "r": T * (0.45 + 0.2 * self.energy), "life": 0.16,
                                "color": WHITE})
            burst = [n for n in ("scorch_01", "scorch_02") if n in self.images]
            star = [n for n in ("star_07", "star_06") if n in self.images]
            if burst:  # 素材: 放射状の火花の爆発(一瞬で開いて消える)
                self.sprites.append({"t0": t, "name": self.rng.choice(burst), "x": x, "y": y, "vy": 0.0,
                                     "size": (T * 1.1 * k, T * 1.6 * k), "angle": self.rng.uniform(0, 360), "spin": 0.0,
                                     "life": 0.18, "color": mix(WHITE, color, 0.45), "opacity": 1.0, "additive": True})
            if star:  # 素材: 十字の閃光(大きく光ってすぐ縮む)
                self.sprites.append({"t0": t, "name": star[0], "x": x, "y": y, "vy": 0.0,
                                     "size": (T * 1.7 * k, T * 0.9 * k), "angle": math.degrees(-ang) + 45 * self.rng.random(),
                                     "spin": 0.0, "life": 0.12, "color": WHITE, "opacity": 1.0, "additive": True})
            # 放射状に一瞬伸びる光の筋(この版では素材の画像を描かないので、毎回これがパンチの演出の中心)
            n = 10
            base = self.rng.uniform(0, 2 * math.pi)
            self.streaks.append({"t0": self.fx_clock, "x": x, "y": y, "life": 0.2, "color": color,
                                 "rays": [(base + math.pi * 1.3 * i / max(1, n - 1) + self.rng.uniform(-0.10, 0.10),
                                           T * k * self.rng.uniform(0.6, 1.05)) for i in range(n)]})
            # 軽量な炎。画像、ぼかし、拡大縮小は使わず、小さな円と短線だけで描く。
            for _ in range(int(5 + 4 * self.energy)):
                a = ang + self.rng.gauss(0, 0.55)
                speed = self.rng.uniform(180, 520) * u
                self._add(Particle(x + self.rng.uniform(-0.08, 0.08) * T,
                                   y + self.rng.uniform(-0.08, 0.08) * T,
                                   math.cos(a) * speed,
                                   math.sin(a) * speed - self.rng.uniform(20, 80) * u,
                                   self.rng.uniform(0.18, 0.38), "flame",
                                   self.rng.choice([WARM, WARM, (30, 180, 255)]),
                                   self.rng.uniform(2.0, 4.2) * u, drag=4.2, grav=-90 * u))
            for _ in range(3):
                a = ang + self.rng.gauss(0, 0.75)
                speed = self.rng.uniform(420, 850) * u
                self._add(Particle(x, y, math.cos(a) * speed, math.sin(a) * speed,
                                   self.rng.uniform(0.10, 0.20), "spark",
                                   self.rng.choice([WHITE, color]), 1, drag=5.5, grav=220 * u))
            self.flash = max(self.flash, 0.035 + 0.035 * self.energy)
            self.impact_pulse = max(self.impact_pulse, 0.20 + 0.12 * self.energy)
            self.bag_glow = 1.0
            self._echo_requests.append((color, 0.25, 0.4))

    # ------------------------------------------------------------------ 背景・人

    def _person_mask(self, w, h, kp, person, bag):
        """人とバッグのところが 1、背景が 0 のぼかしたマスク(骨格を太い線で描いて作る。1/4 の大きさ)。"""
        s = 4
        T = self.torso / s
        m = np.zeros((h // s, w // s), np.uint8)
        if kp:
            P = {i: (int(p[0] / s), int(p[1] / s)) for i, p in kp.items()}
            th = max(2, int(T * 0.65))
            for a, b in BODY_LINES:
                if a in P and b in P:
                    cv2.line(m, P[a], P[b], 255, th)
            quad = [P[i] for i in (L_SHOULDER, R_SHOULDER, R_HIP, L_HIP) if i in P]
            if len(quad) == 4:
                cv2.fillConvexPoly(m, np.array(quad, np.int32), 255)
            for i in (L_WRIST, R_WRIST):
                if i in P:
                    cv2.circle(m, P[i], max(2, int(T * 0.4)), 255, -1)
            head = self._head(kp)
            if head is not None:
                hc = (int(head[0] / s), int(head[1] / s))
                cv2.circle(m, hc, max(3, int(T * 0.55)), 255, -1)
                neck = self._mid(kp, L_SHOULDER, R_SHOULDER)
                if neck is not None:
                    cv2.line(m, hc, (int(neck[0] / s), int(neck[1] / s)), 255, th)
        elif person is not None:
            x0, y0, x1, y1 = (v / s for v in person.box)
            cv2.ellipse(m, ((x0 + x1) / 2, (y0 + y1) / 2), ((x1 - x0) / 2, (y1 - y0) / 2), 0, 0, 360, 255, -1)
        if bag is not None:
            c = (bag.contour // s).astype(np.int32)
            cv2.fillPoly(m, [c], 255)
            cv2.drawContours(m, [c], -1, 255, 2)
        m = cv2.GaussianBlur(m, (0, 0), max(1.5, T * 0.18))
        return m.astype(np.float32) * (1 / 255)

    def _motes(self, w, h, t, dt):
        """背景に漂う、ごく淡い光の粒(足し算で重ねる層を返す)。"""
        e = min(1.0, self.energy * 1.2)
        dots = np.zeros((h // 2, w // 2, 3), np.uint8)
        for m in self.motes:
            m["y"] += m["vy"] * dt
            m["x"] += math.sin(t * 0.6 + m["phase"]) * 6 * dt * self.unit
            if m["y"] < -10:
                m.update(self._new_mote(w, h))
            k = 0.28 + 0.18 * math.sin(t * 2 + m["phase"])
            cv2.circle(dots, (int(m["x"] / 2), int(m["y"] / 2)), max(1, int(m["r"])),
                       scaled(mix(COOL, WARM, e), k), -1, cv2.LINE_AA)
        return cv2.resize(cv2.GaussianBlur(dots, (0, 0), 1.0), (w, h), interpolation=cv2.INTER_LINEAR)

    def _rim(self, small, w, h, t):
        """人の輪郭のすぐ外側の細い光(リムライト)。"""
        halo = cv2.GaussianBlur(small, (0, 0), max(1.2, self.torso / 4 * 0.12))
        rim = np.clip(halo * 1.7 - small * 1.5, 0, 1)
        strength = (0.35 + 0.55 * self.energy) * (0.9 + 0.1 * math.sin(t * 4))
        layer = rim[..., None] * (np.array(self._accent(), np.float32) * strength)
        return cv2.resize(np.clip(layer, 0, 255).astype(np.uint8), (w, h), interpolation=cv2.INTER_LINEAR)

    # ------------------------------------------------------------------ 残像

    def _capture_echo(self, frame, small, mask, t, color, life, strength):
        x, y, bw, bh = cv2.boundingRect((small > 0.3).astype(np.uint8))
        if bw == 0 or bh == 0:
            return
        x0, y0, x1, y1 = x * 4, y * 4, min(frame.shape[1], (x + bw) * 4), min(frame.shape[0], (y + bh) * 4)
        gray = cv2.cvtColor(frame[y0:y1, x0:x1], cv2.COLOR_BGR2GRAY)
        self.echoes.append({"t0": t, "x0": x0, "y0": y0, "gray": gray, "alpha": mask[y0:y1, x0:x1].copy(),
                            "color": np.array(color, np.float32) / 255, "life": life, "strength": strength})
        if len(self.echoes) > 6:
            self.echoes.pop(0)

    def _draw_echoes(self, light, mask, t):
        keep = []
        for e in self.echoes:
            f = (t - e["t0"]) / e["life"]
            if f >= 1 or f < 0:
                continue
            keep.append(e)
            a = e["strength"] * (1 - f) ** 1.5
            gh, gw = e["gray"].shape
            y0, x0 = e["y0"], e["x0"]
            weight = e["gray"].astype(np.float32) * e["alpha"] * (1 - mask[y0:y0 + gh, x0:x0 + gw]) * a
            layer = np.clip(weight[..., None] * e["color"], 0, 255).astype(np.uint8)
            light[y0:y0 + gh, x0:x0 + gw] = cv2.add(light[y0:y0 + gh, x0:x0 + gw], layer)
        self.echoes = keep

    # ------------------------------------------------------------------ 光る線・粒

    def _add(self, p):
        if len(self.particles) < self.MAX_PARTICLES:
            self.particles.append(p)

    def _step_particles(self, light, dt):
        h, w = light.shape[:2]
        alive = []
        for p in self.particles:
            p.age += dt
            if p.age >= p.life:
                continue
            k = math.exp(-p.drag * dt)
            p.vx *= k
            p.vy = p.vy * k + p.grav * dt
            p.x += p.vx * dt
            p.y += p.vy * dt
            if not (-50 < p.x < w + 50 and -50 < p.y < h + 50):
                continue
            alive.append(p)
            f = p.age / p.life
            if p.kind == "spark":
                c = scaled(mix(WHITE, p.color, f), 1 - f)
                cv2.line(light, (int(p.x), int(p.y)), (int(p.x - p.vx * 0.035), int(p.y - p.vy * 0.035)), c, 1,
                         cv2.LINE_AA)
            elif p.kind == "flame":
                fade = (1 - f) ** 1.35
                radius = max(1, int(p.size * (0.45 + 0.55 * fade)))
                tail = (int(p.x - p.vx * 0.018), int(p.y - p.vy * 0.018))
                cv2.line(light, tail, (int(p.x), int(p.y)), scaled((20, 90, 255), 0.55 * fade),
                         max(1, radius), cv2.LINE_AA)
                cv2.circle(light, (int(p.x), int(p.y)), radius, scaled(p.color, 0.82 * fade), -1, cv2.LINE_AA)
                if radius >= 3:
                    cv2.circle(light, (int(p.x), int(p.y)), max(1, radius // 3),
                               scaled((180, 245, 255), fade), -1, cv2.LINE_AA)
            else:  # mote / ember
                k = (1 - f) * (0.6 + 0.4 * math.sin(p.age * 20)) if p.kind == "ember" else (1 - f) * 0.8
                cv2.circle(light, (int(p.x), int(p.y)), max(1, int(p.size)), scaled(p.color, k), -1, cv2.LINE_AA)
        self.particles = alive

    def _draw_trails(self, light, t):
        # 一本の太い蛍光線ではなく、芯と薄い外光を分けたテーパー状の軌跡。
        for side, trail in self.trails.items():
            pts = list(trail)
            for i, ((_, x0, y0), (t1, x1, y1)) in enumerate(zip(pts, pts[1:])):
                f = min(1.0, (t - t1) / 0.22)
                age = (1 - f) ** 1.7
                taper = (i + 1) / max(1, len(pts) - 1)
                outer = max(1, int((5.0 * self.unit) * age * taper))
                core = max(1, int((1.6 * self.unit) * age * taper))
                p0, p1 = (int(x0), int(y0)), (int(x1), int(y1))
                cv2.line(light, p0, p1, scaled(SIDE[side], 0.23 * age), outer, cv2.LINE_AA)
                cv2.line(light, p0, p1, scaled(mix(WHITE, SIDE[side], 0.35), 0.92 * age), core, cv2.LINE_AA)

    def _draw_rings(self, light, t):
        keep = []
        for r in self.rings:
            f = (t - r["t0"]) / r["life"]
            if f >= 1 or f < 0:
                continue
            keep.append(r)
            R = max(1.0, r["r"] * ease_out(f))
            cv2.ellipse(light, (int(r["x"]), int(r["y"])), (int(R), max(1, int(R * r["aspect"]))), r["angle"], 0, 360,
                        scaled(r["color"], (1 - f) ** 1.3), r["thick"], cv2.LINE_AA)
        self.rings = keep

    def _draw_flares(self, light, t):
        """衝撃点のやわらかい光(中心が白く、外へ向かって消える)。"""
        keep = []
        for fl in self.flares:
            f = (t - fl["t0"]) / fl["life"]
            if f >= 1 or f < 0:
                continue
            keep.append(fl)
            R = max(4, int(fl["r"] * (0.7 + 0.5 * ease_out(f))))
            g = _glow_sprite(R) * ((1 - f) ** 2)
            x0, y0 = int(fl["x"]) - R, int(fl["y"]) - R
            h, w = light.shape[:2]
            ax0, ay0, ax1, ay1 = max(0, x0), max(0, y0), min(w, x0 + 2 * R + 1), min(h, y0 + 2 * R + 1)
            if ax1 <= ax0 or ay1 <= ay0:
                continue
            patch = g[ay0 - y0:ay1 - y0, ax0 - x0:ax1 - x0, None] * np.array(fl["color"], np.float32)
            light[ay0:ay1, ax0:ax1] = cv2.add(light[ay0:ay1, ax0:ax1], patch.astype(np.uint8))
        self.flares = keep

    def _draw_sprites(self, light, out, t, additive):
        """素材の画像を描く。additive=True は光(light に足す)、False は土ぼこり(out に色を重ねる)。"""
        for sp in self.sprites:
            f = (t - sp["t0"]) / sp["life"]
            if f >= 1 or f < 0 or sp["additive"] != additive:
                continue
            img = self.images[sp["name"]]
            size = sp["size"][0] + (sp["size"][1] - sp["size"][0]) * ease_out(f)
            k = sp["opacity"] * ((1 - f) ** 1.5 if additive else math.sin(math.pi * min(1.0, f * 1.4 + 0.15)) * (1 - f))
            age = t - sp["t0"]
            cx, cy = sp["x"], sp["y"] + sp["vy"] * age
            src = img.shape[0]
            n = max(4, int(size * 1.45) + 2)  # 回しても切れない大きさ
            M = cv2.getRotationMatrix2D((src / 2, src / 2), sp["angle"] + sp["spin"] * age, size / src)
            M[0, 2] += n / 2 - src / 2
            M[1, 2] += n / 2 - src / 2
            patch = cv2.warpAffine(img, M, (n, n), flags=cv2.INTER_LINEAR, borderValue=0)
            x0, y0 = int(cx - n / 2), int(cy - n / 2)
            h, w = out.shape[:2]
            ax0, ay0, ax1, ay1 = max(0, x0), max(0, y0), min(w, x0 + n), min(h, y0 + n)
            if ax1 <= ax0 or ay1 <= ay0:
                continue
            a = patch[ay0 - y0:ay1 - y0, ax0 - x0:ax1 - x0, None] * k
            color = np.array(sp["color"], np.float32)
            if additive:
                light[ay0:ay1, ax0:ax1] = cv2.add(light[ay0:ay1, ax0:ax1], (a * color).astype(np.uint8))
            else:
                roi = out[ay0:ay1, ax0:ax1]
                roi[:] = (roi * (1 - a) + color * a).astype(np.uint8)

    def _draw_streaks(self, out, t):
        """パンチの光の筋: 一瞬で外へ伸び、根元から消えていく(輪は残さない)。
        足し算の光は白い背景で見えないので、画面に直接、色で描く(縁を少し濃い色にして、白い所でも輪郭が分かるように)。"""
        keep = []
        for st in self.streaks:
            f = (t - st["t0"]) / st["life"]
            if f >= 1 or f < 0:
                continue
            keep.append(st)
            grow = ease_out(min(1.0, f / 0.35))
            th = max(1, int(round(2.6 * self.unit * (1 - f) ** 0.7)))
            edge = scaled(st["color"], 0.55)
            core = mix(WHITE, st["color"], 0.25)
            for a, length in st["rays"]:
                outer = length * (0.35 + 0.65 * grow)
                inner = outer * (0.2 + 0.7 * f)  # 根元から消えていく
                ca, sa = math.cos(a), math.sin(a)
                p0 = (int(st["x"] + ca * inner), int(st["y"] + sa * inner))
                p1 = (int(st["x"] + ca * outer), int(st["y"] + sa * outer))
                cv2.line(out, p0, p1, edge, th + 1, cv2.LINE_AA)
                cv2.line(out, p0, p1, st["color"], th, cv2.LINE_AA)
                if th >= 3:
                    cv2.line(out, p0, p1, core, max(1, th // 3), cv2.LINE_AA)
        self.streaks = keep

    def _impact_grade(self, frame, amount):
        """キャッシュ済みマスクを使う軽量版。色変換をやめ、周辺減光だけで重量感を出す。"""
        if self._vignette is None:
            return frame
        gain = 1.0 - self._vignette * (0.22 * amount)
        # cv2.multiply は NumPy の複数巨大一時配列より軽い。
        return cv2.multiply(frame, gain[..., None], dtype=cv2.CV_8U)

    # ------------------------------------------------------------------ 数字の表示

    def _draw_hud(self, out, t, result):
        h, w = out.shape[:2]
        u = self.unit
        margin = 26 * u
        accent = self._accent()

        # 右上: コンボ(2 以上のときだけ)。数字と、次のパンチまでの残り時間の細い線
        since = t - self.last_punch_t
        if self.combo >= 2 and since < self.COMBO_GAP + 0.4:
            appear = ease_out(min(1.0, max(0.0, t - self.combo_start) / 0.25))
            fade = 1.0 if since < self.COMBO_GAP else max(0.0, 1 - (since - self.COMBO_GAP) / 0.4)
            opacity = fade * appear
            right = w - margin + (1 - appear) * 30 * u
            num = self._sprite(str(self.combo), int(58 * u), WHITE, bold=True)
            tick = 1.0 + 0.1 * max(0.0, 1 - since / 0.12)
            nh, nw = num[1].shape
            label = self._sprite("COMBO", int(15 * u), (210, 210, 210))
            lh, lw = label[1].shape
            self._blit(out, *label, right - lw / 2, margin + lh / 2, 1.0, opacity)
            ny = margin + lh + nh / 2
            self._blit(out, *num, right - nw * tick / 2, ny, tick, opacity)
            ly = int(ny + nh / 2 + 2 * u)
            length = 110 * u
            remain = max(0.0, 1 - since / self.COMBO_GAP)
            cv2.line(out, (int(right - length), ly), (int(right), ly), scaled((110, 110, 110), opacity), 1, cv2.LINE_AA)
            cv2.line(out, (int(right - length * remain), ly), (int(right), ly), scaled(accent, opacity), 2, cv2.LINE_AA)

        # 左下: パンチ数(合計と左右)
        x = margin
        label = self._sprite("PUNCHES", int(13 * u), (200, 200, 200))
        total = self._sprite(str(result.punches), int(40 * u), WHITE, bold=True)
        sides = self._sprite(f"L {self.counts.get('L', 0)}     R {self.counts.get('R', 0)}", int(14 * u), (220, 220, 220))
        lh, lw = label[1].shape
        th_, tw = total[1].shape
        sh, sw = sides[1].shape
        y_sides = h - margin - sh / 2
        y_total = y_sides - sh / 2 - th_ / 2
        y_label = y_total - th_ / 2 - lh / 2
        cv2.line(out, (int(x), int(y_label - lh / 2 + 4 * u)), (int(x), int(h - margin - 4 * u)), accent, 2, cv2.LINE_AA)
        x += 12 * u
        self._blit(out, *label, x + lw / 2, y_label, 1.0, 0.9)
        self._blit(out, *total, x + tw / 2, y_total, 1.0, 1.0)
        self._blit(out, *sides, x + sw / 2, y_sides, 1.0, 0.9)

    def _font(self, size, bold):
        key = (size, bold)
        if key not in self._fonts:
            font = ImageFont.truetype(self.font_path, size)
            try:  # Bahnschrift は太さを選べる(可変フォント)
                names = [n.decode() if isinstance(n, bytes) else n for n in font.get_variation_names()]
                want = "Bold" if bold else "SemiLight"
                if want in names:
                    font.set_variation_by_name(want)
            except Exception:
                pass
            self._fonts[key] = font
        return self._fonts[key]

    def _sprite(self, text, size, color, bold=False):
        """影つきの文字の画像 (BGR, 不透明度 0〜1)。同じものは使い回す。"""
        key = (text, size, color, bold)
        if key in self._sprites:
            return self._sprites[key]
        if len(self._sprites) > 200:
            self._sprites.clear()
        if self.font_path:
            font = self._font(size, bold)
            x0, y0, x1, y1 = ImageDraw.Draw(Image.new("L", (1, 1))).textbbox((0, 0), text, font=font)
            pad = max(4, size // 6)
            img = Image.new("L", (x1 - x0 + 2 * pad, y1 - y0 + 2 * pad), 0)
            ImageDraw.Draw(img).text((pad - x0, pad - y0), text, font=font, fill=255)
            glyph = np.array(img)
        else:
            fs = size / 32
            th = 2 if bold else 1
            (tw, tht), base = cv2.getTextSize(text, cv2.FONT_HERSHEY_DUPLEX, fs, th)
            pad = 6
            glyph = np.zeros((tht + base + 2 * pad, tw + 2 * pad), np.uint8)
            cv2.putText(glyph, text, (pad, pad + tht), cv2.FONT_HERSHEY_DUPLEX, fs, 255, th, cv2.LINE_AA)
        text_a = glyph.astype(np.float32) / 255
        shadow = cv2.GaussianBlur(np.roll(np.roll(text_a, 1, 0), 1, 1), (0, 0), max(1.0, size / 18)) * 0.6
        alpha = np.maximum(text_a, shadow)
        bgr = (text_a[..., None] / np.maximum(alpha, 1e-3)[..., None] * np.array(color, np.float32)).astype(np.uint8)
        self._sprites[key] = (bgr, alpha)
        return bgr, alpha

    @staticmethod
    def _blit(dst, bgr, alpha, cx, cy, scale=1.0, opacity=1.0):
        if scale <= 0.02 or opacity <= 0.01:
            return
        if abs(scale - 1) > 1e-3:
            bgr = cv2.resize(bgr, None, fx=scale, fy=scale, interpolation=cv2.INTER_LINEAR)
            alpha = cv2.resize(alpha, None, fx=scale, fy=scale, interpolation=cv2.INTER_LINEAR)
        h, w = alpha.shape
        x0, y0 = int(round(cx - w / 2)), int(round(cy - h / 2))
        H, W = dst.shape[:2]
        dx0, dy0, dx1, dy1 = max(0, x0), max(0, y0), min(W, x0 + w), min(H, y0 + h)
        if dx1 <= dx0 or dy1 <= dy0:
            return
        a = alpha[dy0 - y0:dy1 - y0, dx0 - x0:dx1 - x0, None] * opacity
        roi = dst[dy0:dy1, dx0:dx1]
        roi[:] = (roi * (1 - a) + bgr[dy0 - y0:dy1 - y0, dx0 - x0:dx1 - x0] * a).astype(np.uint8)


class ScreenView:
    """画面に出すもの(エフェクトと検出結果の表示)をまとめる。ウィンドウで E = エフェクト、D = 検出結果の表示 を切り替え。"""

    def __init__(self, args):
        self.effects = Effects()
        self.fx_on = not args.no_effects
        self.overlay_on = not args.hide_overlay
        print(f"Effects: {'on' if self.fx_on else 'off'} (E to toggle), "
              f"detection overlay: {'on' if self.overlay_on else 'off'} (D to toggle)")

    def draw(self, frame, pipeline, result, hint=""):
        """frame にエフェクトと検出結果を描いたものを返す(エフェクトがあると別の画像になる)。"""
        from output import draw_overlay

        if self.fx_on:
            counts = pipeline.judge.punch_counts if pipeline.judge.use_hands else None
            frame = self.effects.render(frame, pipeline.bag, pipeline.person, result, counts=counts)
        if self.overlay_on:
            draw_overlay(frame, pipeline.bag, pipeline.person, pipeline.judge, pipeline.motion, result, hint=hint)
        return frame

    def key(self, code):
        k = code & 0xFF
        if k in (ord("e"), ord("E")):
            self.fx_on = not self.fx_on
        elif k in (ord("d"), ord("D")):
            self.overlay_on = not self.overlay_on
