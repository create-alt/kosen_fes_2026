"""判定の部分: ヒット(手とバッグの接触)・パンチ判定・頭と体の動き量・キー押下の判定。

ヒット : 手(手首)を中心とする半径 R の円に、サンドバッグの領域が入っている(接触または重なり)
パンチ : 既定は「接触のみ」: 手の円が新しくバッグに触れたら 1 回。インターバル(cooldown)は左手・右手で別々に数える
         threshold を指定すると、接触の直後(window 秒以内)に、バッグの傾きが静止時から
         threshold 度以上ずれたときだけ。バッグの振動で何度しきい値を超えても、新しい接触が無ければ数えない
動き量 : 頭の上下(上が正)と体の重心の前後(バッグへ近づくと正)の、基準(キャリブレーション時)からのずれ
"""

import math
import statistics
from dataclasses import dataclass
from typing import Optional

import cv2

from common import HEAD_POINTS, SHOULDERS, HIPS

BODY_POINTS = SHOULDERS + HIPS


SIDES = ("L", "R")  # L = 人の左手、R = 人の右手(骨格推定の left_wrist / right_wrist)


def hit_test(contour, hands, radius):
    """手のどれかを中心とする半径 radius の円が、バッグの領域と接触または重なっているか。
    hands: [(x, y), ...](リスト)。"""
    for x, y in hands:
        # 符号つき距離: 領域の内側なら正、外側なら負。外側へ radius 以内なら接触
        if cv2.pointPolygonTest(contour, (float(x), float(y)), True) >= -radius:
            return True
    return False


class _HandState:
    """片手ぶんの状態。インターバル(cooldown)は手ごとに数えるので、左右で連打しても互いに邪魔しない。"""

    def __init__(self):
        self.last_touch = -1e9  # 最後に円がバッグに入っていた時刻
        self.contact_time = None  # まだパンチに使っていない接触の時刻(傾き条件ありのとき)
        self.last_punch = -1e9
        self.show_until = 0.0
        self.hit = False
        self.contacts = 0
        self.punches = 0


class PunchJudge:
    """バッグの傾きの基準と、左右それぞれの手の接触から、パンチを判定する。"""

    def __init__(
        self,
        use_hands=True,
        threshold=None,
        cooldown=0.5,
        calib_frames=30,
        hand_radius=None,
        hand_radius_ratio=0.5,
        window=0.3,
        pre_window=0.1,
        release=0.15,
        sway_max=None,
        cooldown_left=None,
        cooldown_right=None,
        cooldown_any=0.0,
    ):
        """
        use_hands         : True なら手の接触を条件にする。False なら傾きだけで判定(従来方式)
        threshold         : None なら手の接触だけでパンチ(傾きは見ない)。数値なら、接触に加えて静止時からの
                            傾きのずれ(度)がこの値以上のときパンチ。手を使わない(傾きだけの)判定では None は 8.0
        cooldown          : 同じ手でパンチと判定してから、その手で次を判定するまでの最短時間(秒)
        cooldown_left/right : 左手 / 右手だけ別の値にしたいとき。None なら cooldown と同じ
        cooldown_any      : 左右を問わず、どちらかの手でパンチと判定してから次までの最短時間(秒)。
                            既定 0 = 左右の連打を妨げない。両手が同時に当たって 2 回数えられるのを防ぎたいときだけ増やす
        calib_frames      : 静止時の傾き(基準)を測るフレーム数
        hand_radius       : 手を中心とする円の半径(画素)。None なら バッグの幅 × hand_radius_ratio
        window / pre_window : 接触の後 / 直前の、この秒数以内の揺れをパンチとする(threshold 指定時)
        release           : その手がこの秒数以上離れてから触れたら、新しい接触とする(検出のちらつき対策)
        sway_max          : 揺れの上限(度)。超えるずれはパンチの揺れとみなさない。None なら上限なし
        """
        self.use_hands, self.cooldown_any = use_hands, cooldown_any
        self.cooldown = {"L": cooldown if cooldown_left is None else cooldown_left,
                         "R": cooldown if cooldown_right is None else cooldown_right}
        self._base_cooldown = cooldown  # 傾きだけの判定で使う
        self.contact_only = use_hands and threshold is None  # 接触だけでパンチと判定する
        self.threshold = threshold if threshold is not None else (None if use_hands else 8.0)
        self.calib_frames = calib_frames
        self.hand_radius, self.hand_radius_ratio = hand_radius, hand_radius_ratio
        self.window, self.pre_window, self.release, self.sway_max = window, pre_window, release, sway_max

        self.baseline: Optional[float] = None  # 静止時の傾き
        self.deviation = 0.0  # 基準からのずれ(度)
        self.punch_count = 0  # 左右合計
        self.hit: Optional[bool] = None  # どちらかの手の円にバッグが入っているか(判定不能は None)
        self.radius = None  # 直近に使った円の半径(画素)
        self.hands = {}  # 直近の手の位置 {"L": (x, y), "R": (x, y)}(見えている手だけ。表示用)
        self.punch_sides = []  # 直近のフレームでパンチと判定した手 ["L"], ["R"], ["L", "R"] または []
        self._hand = {s: _HandState() for s in SIDES}

        self.calibrating = False  # C キー(など)で、いま基準を測り直している間
        self._recal = []
        self._calib = []
        self._armed = True
        self._last_punch_any = -1e9
        self._last_exceed = -1e9
        self._angle = 0.0

    def set_calibrating(self, active):
        """
        active=True の間のバッグの傾きを集め、False にしたとき、その中央値を新しい基準(静止時の傾き)にする。
        測っている間は、パンチの判定はしない(基準を測るための時間なので)。起動直後の自動の基準測定は、
        これを使わなくても行われ(calib_frames)、あとからこれで測り直すと置き換わる。
        """
        if active and not self.calibrating:
            self.calibrating, self._recal = True, []
        elif not active and self.calibrating:
            self.calibrating = False
            if self._recal:
                self.baseline = statistics.median(self._recal)
                self.deviation = 0.0
            self._recal = []
            self._armed = True
            for h in self._hand.values():  # 測っている間の接触は、パンチに使わない
                h.contact_time = None

    # ----- 参照用
    @property
    def calibrated(self):
        return self.baseline is not None

    @property
    def calib_progress(self):
        return len(self._calib)

    @property
    def contact_count(self):
        return sum(h.contacts for h in self._hand.values())

    @property
    def punch_counts(self):
        return {s: self._hand[s].punches for s in SIDES}

    @property
    def contact_counts(self):
        return {s: self._hand[s].contacts for s in SIDES}

    @property
    def hit_sides(self):
        """いま円にバッグが入っている手 {"L": bool, "R": bool}。"""
        return {s: self._hand[s].hit for s in SIDES}

    def should_show_punch(self, now, side=None):
        """PUNCH! を表示している間か。side を指定すると、その手のパンチだけ。"""
        if side is not None:
            return now < self._hand[side].show_until
        return any(now < h.show_until for h in self._hand.values())

    # ----- 内部
    def _radius_for(self, contour):
        if self.hand_radius is not None:
            return float(self.hand_radius)
        _, _, w, h = cv2.boundingRect(contour)
        return self.hand_radius_ratio * min(w, h)  # バッグは縦長なので、短い方(幅)

    def _allowed(self, side, now):
        return (now - self._hand[side].last_punch >= self.cooldown[side]
                and now - self._last_punch_any >= self.cooldown_any)

    def _punch(self, side, now):
        h = self._hand[side]
        h.punches += 1
        h.last_punch = now
        h.show_until = now + 0.5
        self.punch_count += 1
        self._last_punch_any = now
        self.punch_sides.append(side)

    def _touch(self, side, now):
        """この手の円がバッグに入っているときの処理。新しい接触なら True を返す。"""
        h = self._hand[side]
        new_contact = False
        if h.hit:
            if now - h.last_touch >= self.release:
                h.contacts += 1
                new_contact = True
            h.last_touch = now
        return new_contact

    def update(self, bag, hands, now):
        """
        1フレームぶんを処理し、どちらかの手でパンチと判定したら True を返す(どちらの手かは punch_sides)。
        bag: BagPose または None / hands: {"L": (x, y), "R": (x, y)}(見えている手だけ)
        """
        self.hit = None
        self.punch_sides = []
        for h in self._hand.values():
            h.hit = False
        if bag is None:
            return False

        # ヒットは、傾きの基準を測っている間も、毎フレーム求める(手ごとに)
        self.hands = dict(hands or {})
        if self.use_hands:
            self.radius = self._radius_for(bag.contour)
            for side, point in self.hands.items():
                self._hand[side].hit = hit_test(bag.contour, [point], self.radius)
            self.hit = any(h.hit for h in self._hand.values())

        self._angle = bag.angle_deg
        if self.calibrating:  # 基準を測り直している間は、傾きを集めるだけ(パンチは判定しない)
            self._recal.append(bag.angle_deg)
            return False
        if self.contact_only:
            return self._contact_only(now)

        # 静止時の傾きの基準
        if not self.calibrated:
            self._calib.append(bag.angle_deg)
            if len(self._calib) >= self.calib_frames:
                self.baseline = statistics.median(self._calib)
            return False
        self.deviation = bag.angle_deg - self.baseline
        magnitude = abs(self.deviation)

        if not self.use_hands:
            return self._tilt_only(magnitude, now)

        exceeding = magnitude >= self.threshold and (self.sway_max is None or magnitude <= self.sway_max)
        if exceeding:
            self._last_exceed = now
        for side in SIDES:
            h = self._hand[side]
            new_contact = self._touch(side, now)
            if new_contact:
                h.contact_time = now
            if h.contact_time is None:
                continue
            if now - h.contact_time > self.window:
                h.contact_time = None  # 接触から時間が経った。揺れが無かった(軽く触れただけ)
                continue
            # 揺れが接触の直後に起きた、または接触の直前から始まっていた
            if not (exceeding or (new_contact and h.contact_time - self._last_exceed <= self.pre_window)):
                continue
            h.contact_time = None  # 1回の接触では1回だけ
            if self._allowed(side, now):
                self._punch(side, now)
        return bool(self.punch_sides)

    def _contact_only(self, now):
        """接触だけで判定: その手の円が新しくバッグに触れた瞬間に 1 回(手ごとのインターバルつき)。
        傾きの基準は表示用に測り続ける。"""
        if not self.calibrated:
            self._calib.append(self._angle)
            if len(self._calib) >= self.calib_frames:
                self.baseline = statistics.median(self._calib)
        else:
            self.deviation = self._angle - self.baseline
        for side in SIDES:
            if self._touch(side, now) and self._allowed(side, now):
                self._punch(side, now)
        return bool(self.punch_sides)

    def _tilt_only(self, magnitude, now):
        """傾きだけで判定する方式: しきい値を超えたら1回。静止位置付近まで戻ると次を受け付ける。
        手を使わないので左右の区別はなく、cooldown(共通)で間隔を空ける。"""
        if not self._armed and magnitude <= self.threshold * 0.5:
            self._armed = True
        if self._armed and magnitude >= self.threshold and now - self._last_punch_any >= self._base_cooldown:
            self._armed = False
            self.punch_count += 1
            self._last_punch_any = now
            for h in self._hand.values():
                h.show_until = now + 0.5
            self.punch_sides = ["L", "R"]  # 手は不明
            return True
        return False


# ------------------------------------------------------------------ 頭と体の動き量


def _mean_of(values):
    return sum(values) / len(values) if values else None


@dataclass
class MotionReading:
    """基準からのずれ。見えていない項目は None。*_px は画素数、他は基準の体幹の長さを 1 とした値。"""

    vertical_px: Optional[float] = None  # 頭の上下(上が正)
    forward_px: Optional[float] = None  # 体の重心の前後(バッグへ近づくと正)
    vertical: Optional[float] = None
    forward: Optional[float] = None
    depth: Optional[float] = None  # 手前・奥: 手前 = -1、基準 = 0、奥 = +1(体幹の長さの比から)
    size_ratio: Optional[float] = None  # いまの体幹の長さ ÷ 基準の体幹の長さ(1 より大きいほど手前)


class BodyMotion:
    """
    頭(上下)と体の重心(前後)の動きを、キャリブレーション時の位置からのずれとして求める。

    基準は点ごと(鼻・耳・肩・腰それぞれ)に記録し、動き量は「いま見えている点の、基準からの
    ずれの平均」で求める。横向きで奥側の点が見え隠れしても、値が飛ばない。
    キャリブレーション: auto_frames を指定すると最初のそのフレーム数で自動(動画・--no-display)。
    None なら set_calibrating(True/False) で開始・終了(リアルタイム。押している間に集めた位置の中央値)。
    """

    def __init__(self, auto_frames=None, smoothing=0.5, forward_direction="auto", min_samples=5,
                 depth_range=1.5):
        """depth_range : 体幹の長さが基準の何倍(または 1/何倍)になったら、depth を -1(または +1)にするか(1 より大きい値)"""
        self.auto_frames, self.smoothing = auto_frames, smoothing
        self.depth_range = max(1.01, depth_range)
        self.forward_direction, self.min_samples = forward_direction, min_samples
        self.calibrating = auto_frames is not None
        self.progress = 0  # キャリブレーションに使ったフレーム数(表示用)
        self.base = {}  # 点の番号 -> 基準の位置
        self.scale = None  # 基準の体幹の長さ(正規化用)
        self.forward_sign = 1.0  # +1: 画像の右が前 / -1: 左が前
        self.bag_ref_x = None  # キャリブレーション時のバッグ中心の x(画素)
        self.body_base_x = None  # 基準の体の x(肩・腰の基準の平均)
        self.head_base_y = None  # 基準の頭の y(頭の点の基準の平均)
        self._reset()
        self._smooth = {}

    def _reset(self):
        self._samples, self._scales, self._body_x, self._bag_x = {}, [], [], []

    @property
    def calibrated(self):
        return bool(self.base)

    def set_calibrating(self, active):
        """active=True の間の頭と体の位置を集め、False にしたとき中央値を基準にする。"""
        if active and not self.calibrating:
            self.calibrating, self.progress = True, 0
            self._reset()
        elif not active and self.calibrating:
            self._finish()

    def _finish(self):
        self.calibrating = False
        for group in (HEAD_POINTS, BODY_POINTS):
            found = {i: p for i in group if len(p := self._samples.get(i, [])) >= self.min_samples}
            if found:  # 測れたときは、古い基準を丸ごと置き換える
                for i in group:
                    self.base.pop(i, None)
                for i, positions in found.items():
                    self.base[i] = (statistics.median(p[0] for p in positions),
                                    statistics.median(p[1] for p in positions))
        if self._scales:
            self.scale = statistics.median(self._scales)
        if self.forward_direction != "auto":
            self.forward_sign = -1.0 if self.forward_direction == "left" else 1.0
        elif self._body_x and self._bag_x:  # 基準のときの、体からバッグへの向き
            self.forward_sign = 1.0 if statistics.median(self._bag_x) >= statistics.median(self._body_x) else -1.0
        self.body_base_x = _mean_of([self.base[i][0] for i in BODY_POINTS if i in self.base])
        self.head_base_y = _mean_of([self.base[i][1] for i in HEAD_POINTS if i in self.base])
        if self._bag_x:
            self.bag_ref_x = statistics.median(self._bag_x)
        self._smooth = {}
        self.auto_frames = None  # 最初の自動の基準測定は一度だけ。以降の測り直しは set_calibrating で行う
        self._reset()

    def normalized(self, reading, width, height):
        """Unity へ送る値 (forward, head)。見積もれないものは None。どちらも 0〜1 に収める。

        forward: キャリブレーション時のバッグの位置を 1、プレイヤー側の画面の端(プレイヤーが左なら左端、
                 右なら右端)を 0 とした、体の重心の位置。
        head   : 画面の下を 0、上を 1 とした、頭の高さ。
        どちらも、見えている点の「基準からのずれ」に基準の位置を足した値なので、横向きで点が隠れても飛びにくい。
        """
        clamp = lambda v: max(0.0, min(1.0, v))
        forward = head = None
        if reading.forward_px is not None and self.body_base_x is not None and self.bag_ref_x is not None:
            x = self.body_base_x + self.forward_sign * reading.forward_px
            edge = 0.0 if self.forward_sign > 0 else float(width)  # 前が右 = プレイヤーは左 = 左端が 0
            span = self.bag_ref_x - edge
            if abs(span) > 1.0:
                forward = clamp((x - edge) / span)
        if reading.vertical_px is not None and self.head_base_y is not None and height:
            head = clamp(1.0 - (self.head_base_y - reading.vertical_px) / height)
        return forward, head

    def update(self, person, bag_x=None) -> MotionReading:
        """person: PersonPose または None / bag_x: バッグ中心の x(「前」の向きを決める)"""
        visible = person.visible_points(HEAD_POINTS + BODY_POINTS) if person is not None else {}

        if self.calibrating:
            self.progress += 1
            for i, p in visible.items():
                self._samples.setdefault(i, []).append(p)
            body = person.body_center if person is not None else None
            if body is not None and bag_x is not None:
                self._body_x.append(body[0])
                self._bag_x.append(bag_x)
            if person is not None and person.torso_length:
                self._scales.append(person.torso_length)
            if self.auto_frames is not None and self.progress >= self.auto_frames:
                if any(len(v) >= self.min_samples for v in self._samples.values()):
                    self._finish()
                else:  # 人が映っていなかった。もう少し待つ
                    self.progress = 0
                    self._reset()
            return MotionReading()

        head = [self.base[i][1] - visible[i][1] for i in HEAD_POINTS if i in visible and i in self.base]
        body = [visible[i][0] - self.base[i][0] for i in BODY_POINTS if i in visible and i in self.base]
        reading = MotionReading()
        for key, moves, sign in (("vertical", head, 1.0), ("forward", body, self.forward_sign)):
            if not moves:
                continue
            raw = sign * sum(moves) / len(moves)
            previous = self._smooth.get(key)
            value = raw if previous is None else self.smoothing * raw + (1 - self.smoothing) * previous
            self._smooth[key] = value
            setattr(reading, key + "_px", value)
            if self.scale:
                setattr(reading, key, value / self.scale)
        self._depth(reading, person)
        return reading

    def _depth(self, reading, person):
        """手前・奥を、体幹(肩の中点〜腰の中点)の長さの、基準との比から求める。距離に反比例して見かけの大きさが
        変わるので、比の対数(= 距離の比の対数)を使い、手前と奥を対称にする。-ln(比) / ln(depth_range) を -1〜1 に収める。"""
        if person is None or not person.torso_length or not self.scale:
            return
        raw = math.log(person.torso_length / self.scale)
        previous = self._smooth.get("depth")
        value = raw if previous is None else self.smoothing * raw + (1 - self.smoothing) * previous
        self._smooth["depth"] = value
        reading.size_ratio = math.exp(value)
        reading.depth = max(-1.0, min(1.0, -value / math.log(self.depth_range)))


class HoldKey:
    """
    キーを押している間だけ True になる判定(OpenCV のウィンドウ用)。OpenCV は「押している間」を
    直接は教えてくれないので、キーの自動リピートで定期的に届くキー入力を使い、timeout 秒届かなく
    なったら離したとみなす(押し始めからリピートが始まるまでの間も押していると扱うので、少し長め)。
    """

    def __init__(self, key="c", timeout=0.7):
        self.codes = {ord(key.lower()), ord(key.upper())}
        self.timeout = timeout
        self._last = -1e9

    def feed(self, key_code, now):
        if (key_code & 0xFF) in self.codes:
            self._last = now

    def held(self, now):
        return now - self._last <= self.timeout
