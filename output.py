"""出力の部分: 1フレームぶんの結果(FrameResult)、毎フレームの出力と Unity への送信(Reporter)、画面への描画。

毎フレーム、次の情報を出力する(Reporter):
    head  頭の座標 (x, y)         体の重心の座標 body (x, y)      ← 画素。見えていなければ none / null
    hit   手(手首)を中心とする円に、バッグの領域が重なっているか(1 / 0。骨格推定が無いときは none)
    punch このフレームで「パンチした」と判定したか(ヒットの直後に、バッグが揺れたとき 1 回だけ 1)
他に、手の座標、バッグの傾き、基準からの動き量、パンチの累計を JSON に含める。

Unity へは --udp HOST:PORT で、毎フレーム {"forward","head","feet","punch"} の JSON を 1 パケット(UDP)で送る(全項目は --udp-full)(README の「Unity との通信」を参照)。
"""

import json
import socket
from dataclasses import dataclass, field
from typing import Optional

import cv2

from common import SKELETON, WRISTS


@dataclass
class FrameResult:
    frame: int
    time: float  # 秒
    size: tuple  # 画像の (幅, 高さ)
    head: Optional[tuple] = None  # 頭の座標 (x, y)
    body: Optional[tuple] = None  # 体の重心の座標 (x, y)
    hands: list = field(default_factory=list)  # 手(手首)の座標
    hands_lr: dict = field(default_factory=dict)  # {"L": (x, y), "R": (x, y)} 見えている手(L = 人の左手)
    hit: Optional[bool] = None  # どちらかの手の円とバッグが重なっているか(None = 判定していない)
    hit_sides: dict = field(default_factory=dict)  # {"L": bool, "R": bool} 手ごとのヒット
    punch: bool = False  # このフレームでパンチと判定したか
    punch_sides: list = field(default_factory=list)  # パンチした手 ["L"] / ["R"] / ["L", "R"]
    punches: int = 0  # パンチの累計
    bag_angle: Optional[float] = None  # バッグの傾き(度)。見失ったら None
    bag_deviation: Optional[float] = None  # 静止時からのずれ(度)
    vertical: Optional[float] = None  # 頭の上下(上が正)。基準の体幹を 1 とした値
    forward: Optional[float] = None  # 体の前後(バッグへ近づくと正)
    vertical_px: Optional[float] = None
    forward_px: Optional[float] = None
    game_forward: Optional[float] = None  # Unity 用: バッグの位置(キャリブレーション時)=1、プレイヤー側の画面の端=0 の体の位置
    game_head: Optional[float] = None  # Unity 用: 画面の下=0、上=1 の頭の高さ

    def to_dict(self):
        def pt(p):
            return None if p is None else [round(p[0], 1), round(p[1], 1)]

        def num(v, n=3):
            return None if v is None else round(v, n)

        return {
            "frame": self.frame,
            "t": round(self.time, 3),
            "size": list(self.size),
            "head": pt(self.head),
            "body": pt(self.body),
            "hands": [pt(h) for h in self.hands],
            "hands_lr": {s: pt(self.hands_lr.get(s)) for s in ("L", "R")},
            "hit": None if self.hit is None else int(self.hit),
            "hit_lr": {s: int(self.hit_sides.get(s, False)) for s in ("L", "R")},
            "punch": int(self.punch),
            "punch_hands": list(self.punch_sides),
            "punches": self.punches,
            "unity": self.to_udp_dict(),
            "bag": {"angle": num(self.bag_angle, 2), "deviation": num(self.bag_deviation, 2)},
            "motion": {
                "vertical": num(self.vertical), "forward": num(self.forward),
                "vertical_px": num(self.vertical_px, 1), "forward_px": num(self.forward_px, 1),
            },
        }

    def to_udp_dict(self):
        """Unity へ送る内容: {"forward": 0〜1, "head": 0〜1, "feet": 0.0, "punch": 0/1}。feet は常に 0.0。
        forward / head は、まだ測れていない(キャリブレーション前・人が見えない)ときは null。"""
        def num(v):
            return None if v is None else round(v, 3)

        return {"forward": num(self.game_forward), "head": num(self.game_head), "feet": 0.0, "punch": int(self.punch)}

    def to_text(self):
        def pt(p):
            return "none" if p is None else f"({p[0]:.1f},{p[1]:.1f})"

        hit = "none" if self.hit is None else int(self.hit)
        return (f"frame={self.frame} t={self.time:.3f} head={pt(self.head)} body={pt(self.body)} "
                f"hit={hit} punch={int(self.punch)}" + (f"({''.join(self.punch_sides)})" if self.punch else ""))


class Reporter:
    """毎フレームの結果を、コンソールへ出力し、(指定があれば)UDP で Unity へ送る。"""

    def __init__(self, fmt="text", quiet=False, print_every=1, udp=None, udp_full=False, echo_sent=False):
        """
        fmt         : "text"(1行)または "json"
        quiet       : True ならコンソールには出さない(UDP 送信は続く)
        print_every : コンソールには N フレームに 1 回だけ出す(パンチのあったフレームは必ず出す)
        udp         : "HOST:PORT"。指定すると毎フレーム JSON を送る(既定は forward / head / feet / punch だけ)
        udp_full    : True なら UDP にも全項目(to_dict)を送る
        echo_sent   : True なら、UDP で送った内容(JSON)を、送るたびにそのまま表示する(--quiet で止まる。
                      --print-every は無視して毎回。通常の1行表示の代わり)
        """
        self.fmt, self.quiet, self.print_every = fmt, quiet, max(1, print_every)
        self._sock = self._addr = None
        self._udp_full = udp_full
        self._echo_sent = echo_sent
        if udp:
            host, _, port = udp.rpartition(":")
            if not host or not port.isdigit():
                raise SystemExit(f"--udp must be HOST:PORT (e.g. 127.0.0.1:5005), got '{udp}'")
            self._addr = (host, int(port))
            self._sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            print(f"Sending every frame to Unity via UDP: {host}:{port}")

    def emit(self, result: FrameResult):
        sent = None
        if self._sock is not None:
            sent = json.dumps(result.to_dict() if self._udp_full else result.to_udp_dict(), separators=(",", ":"))
            try:
                self._sock.sendto(sent.encode(), self._addr)
            except OSError:
                pass  # 受け側が居なくても処理は止めない
        if self.quiet:
            return
        if self._echo_sent and sent is not None:
            print(f"frame={result.frame} sent: {sent}")  # 送った値(正規化後)をそのまま表示
        elif not (result.frame % self.print_every and not result.punch):
            print(json.dumps(result.to_dict(), separators=(",", ":")) if self.fmt == "json" else result.to_text())

    def close(self):
        if self._sock is not None:
            self._sock.close()


# ------------------------------------------------------------------ 画面への描画

HEAD, BODY, BONE = (255, 0, 255), (255, 255, 0), (0, 200, 0)
SIDE_COLOR = {"L": (255, 100, 0), "R": (0, 140, 255)}  # BGR: 左手 = 青、右手 = オレンジ
SIDE_NAME = {"L": "Left", "R": "Right"}


def draw_person(frame, person, radius=None, hit=False):
    """骨格(線)、頭の位置、体の重心、手と接触を調べる円を描く。person が None なら何もしない。
    手首の点と円は左右で色を変える(左 = 青、右 = オレンジ)。バッグに触れている手は、円を太くして HIT と表示する。
    hit: bool(両手)、または {"L": bool, "R": bool}。"""
    if person is None:
        return
    keys, ok = person.keypoints, person.keypoints[:, 2] >= person.kpt_conf
    for a, b in SKELETON:
        if ok[a] and ok[b]:
            cv2.line(frame, (int(keys[a][0]), int(keys[a][1])), (int(keys[b][0]), int(keys[b][1])), BONE, 2)
    for i in range(len(keys)):
        if ok[i] and i <= 12 and i not in WRISTS:
            cv2.circle(frame, (int(keys[i][0]), int(keys[i][1])), 3, (255, 255, 255), -1)
    if person.head is not None:
        x, y = (int(v) for v in person.head)
        cv2.circle(frame, (x, y), 9, HEAD, 2)
        cv2.putText(frame, "head", (x + 12, y), cv2.FONT_HERSHEY_SIMPLEX, 0.5, HEAD, 1)
    if person.body_center is not None:
        x, y = (int(v) for v in person.body_center)
        cv2.drawMarker(frame, (x, y), BODY, cv2.MARKER_CROSS, 20, 2)
        cv2.putText(frame, "body", (x + 12, y), cv2.FONT_HERSHEY_SIMPLEX, 0.5, BODY, 1)
    for side, (x, y) in person.hands_by_side.items():
        color = SIDE_COLOR[side]
        touching = bool(hit.get(side)) if isinstance(hit, dict) else bool(hit)
        x, y = int(x), int(y)
        cv2.circle(frame, (x, y), 8 if touching else 6, color, -1)
        cv2.circle(frame, (x, y), 8 if touching else 6, (255, 255, 255), 1)  # 点が背景に埋もれないよう白い縁
        if radius:
            cv2.circle(frame, (x, y), int(radius), color, 5 if touching else 2)  # この円にバッグが入るとヒット(太くなる)
        cv2.putText(frame, side + (" HIT" if touching else ""), (x + 10, y - 10), cv2.FONT_HERSHEY_SIMPLEX,
                    0.6, color, 2)


def draw_overlay(frame, bag, person, judge, motion, result, hint=""):
    """バッグの輪郭・主軸・重心、骨格と手の円、傾き、パンチ数、ヒット、動き量、PUNCH! を描く。"""
    if bag is not None:
        cv2.drawContours(frame, [bag.contour], -1, (0, 255, 0), 2)
        cv2.line(frame, bag.axis[0], bag.axis[1], (255, 0, 0), 3)
        cv2.circle(frame, (int(bag.center[0]), int(bag.center[1])), 5, (0, 0, 255), -1)
    draw_person(frame, person, judge.radius if judge.use_hands else None,
                judge.hit_sides if judge.use_hands else False)

    if judge.calibrating:
        status = f"CALIBRATING (key held)... {len(judge._recal)} frames"
    elif not judge.calibrated and not judge.contact_only:
        status = f"Calibrating... {judge.calib_progress}/{judge.calib_frames}"
    elif bag is None:
        status = "Bag lost"
    elif not judge.calibrated:
        status = f"Tilt: {bag.angle_deg:+.1f}"
    else:
        limit = "" if judge.threshold is None else f" / {judge.threshold:.1f}"
        status = f"Tilt: {bag.angle_deg:+.1f}  Base: {judge.baseline:+.1f}  Dev: {judge.deviation:+.1f}{limit}"
    lines = [(status, (255, 255, 255), 0.65), (f"Punch count: {judge.punch_count}", (0, 255, 255), 0.8)]
    if judge.use_hands:
        counts = judge.punch_counts
        lines.append((f"Left  punches: {counts['L']}   hit: {'YES' if judge.hit_sides['L'] else 'no'}",
                      SIDE_COLOR["L"], 0.6))
        lines.append((f"Right punches: {counts['R']}   hit: {'YES' if judge.hit_sides['R'] else 'no'}",
                      SIDE_COLOR["R"], 0.6))
    if motion is not None:
        if motion.calibrating:
            lines.append((f"Motion: calibrating... ({motion.progress} frames)", BODY, 0.6))
        elif not motion.calibrated:
            lines.append((f"Motion: not calibrated. {hint}".strip(), BODY, 0.6))
        else:
            for label, px, norm in (("Head up(+)/down(-)", result.vertical_px, result.vertical),
                                    ("Body fwd(+)/back(-)", result.forward_px, result.forward)):
                text = f"{label}: lost" if px is None else f"{label}: {px:+.1f}px ({norm:+.2f} torso)"
                lines.append((text, BODY, 0.6))
    if hint and not judge.calibrating and (motion is None or not motion.calibrating):
        lines.append((hint, (180, 180, 180), 0.5))
    y = 35
    for text, color, scale in lines:
        cv2.putText(frame, text, (20, y), cv2.FONT_HERSHEY_SIMPLEX, scale, color, 2)
        y += 30 if scale > 0.7 else 28
    ax, ay = (int(bag.center[0]), int(bag.box[1]) + 40) if bag is not None else (100, 160)  # バッグの上端の少し下
    offset = 0
    for side in ("L", "R"):  # 手ごとに、その手の色で表示(左右が続けてパンチしたら2行になる)
        if judge.should_show_punch(result.time, side):
            cv2.putText(frame, f"PUNCH! {side}", (max(20, ax - 80), max(45, ay - 60) + offset),
                        cv2.FONT_HERSHEY_SIMPLEX, 1.2, SIDE_COLOR[side], 3)
            offset += 42
    return frame
