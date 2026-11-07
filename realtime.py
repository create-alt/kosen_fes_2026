"""リアルタイム用: カメラ映像でパンチ検出・ヒット判定・頭と体の動きを出力する。

    python realtime.py                       # カメラ0。GPU があれば自動で使う
    python realtime.py --device cpu          # CPU で実行
    python realtime.py --udp 127.0.0.1:5005  # 毎フレーム Unity へ UDP で送る
    python realtime.py --no-display --quiet --udp 127.0.0.1:5005   # Unity の裏で動かす(画面・出力なし)
    python realtime.py --stats               # 2秒ごとに fps と処理時間を表示
    python realtime.py bag.mp4               # カメラの代わりに動画ファイル(本番と同じく、等速で流して処理できたフレームだけ使う)

毎フレーム「頭の座標・体の座標・ヒット有無・パンチ有無」を出力する(--format json で JSON)。
起動直後の約 --calib-frames フレームは、バッグを静止させておく(基準の傾きを測るため)。
頭・体の基準位置は、**構えた姿勢で C キーを押している間**に測る(離すと確定。何度でもやり直せる)。
--no-display のときはキーが使えないので、起動直後の --motion-calib-frames フレームで自動で測る。
"""

import argparse
import threading
import time
from pathlib import Path

import cv2
import numpy as np

from args import add_pipeline_args, check_args
from logic import HoldKey
from effects import ScreenView
from output import Reporter, Screen, draw_overlay
from pipeline import Pipeline

WINDOW = "Punching Bag (realtime) (Q: quit, F: fullscreen on/off)"


class ThreadedCamera:
    """
    別スレッドで常に最新のフレームだけを読み続ける(処理が遅れても遅延が溜まらない)。
    source に動画ファイルを渡すと、カメラの代わりに使える: 動画の fps どおりの速さで流し(処理が追いつかないフレームは
    使われない = 本番のカメラと同じ)、幅・高さが width × height に収まるよう縮める(カメラと同じくらいの処理の重さにする)。
    動画は、最初のフレームが読まれた時点から流し始める(モデルの準備中に先へ進まないように)。
    """

    def __init__(self, source=0, width=640, height=480, fps=60, mjpg=True):
        self.is_file = not isinstance(source, int)
        self._cap = cv2.VideoCapture(source)
        if not self._cap.isOpened():
            raise SystemExit(f"Could not open {'video' if self.is_file else 'camera'}: {source}")
        self._size = (width, height)
        if self.is_file:
            self._fps = self._cap.get(cv2.CAP_PROP_FPS) or 30.0
        else:
            if mjpg:  # MJPG にすると USB カメラで高いフレームレートが出やすい
                self._cap.set(cv2.CAP_PROP_FOURCC, cv2.VideoWriter_fourcc(*"MJPG"))
            for prop, value in ((cv2.CAP_PROP_FRAME_WIDTH, width), (cv2.CAP_PROP_FRAME_HEIGHT, height),
                                (cv2.CAP_PROP_FPS, fps), (cv2.CAP_PROP_BUFFERSIZE, 1)):
                if value:
                    self._cap.set(prop, value)
        self._cond = threading.Condition()
        self._frame, self._id, self._time, self._failed = None, 0, 0.0, False
        self._stop = threading.Event()
        self._go = threading.Event()  # 動画ファイル: 最初に読まれたら流し始める
        if not self.is_file:
            self._go.set()
        self._thread = threading.Thread(target=self._loop, daemon=True)

    def start(self):
        self._thread.start()
        return self

    def _shrink(self, frame):
        h, w = frame.shape[:2]
        k = min(1.0, self._size[0] / w if self._size[0] else 1.0, self._size[1] / h if self._size[1] else 1.0)
        return frame if k >= 1.0 else cv2.resize(frame, (int(w * k), int(h * k)), interpolation=cv2.INTER_AREA)

    def _loop(self):
        while not self._go.wait(0.1):
            if self._stop.is_set():
                return
        begin, n = time.monotonic(), 0
        while not self._stop.is_set():
            if self.is_file:  # 動画の時刻まで待つ(カメラと同じ速さで流す)
                wait = begin + n / self._fps - time.monotonic()
                if wait > 0:
                    time.sleep(wait)
                n += 1
            ok, frame = self._cap.read()
            with self._cond:
                if not ok:
                    self._failed = True
                    self._cond.notify_all()
                    return
                self._frame, self._time = (self._shrink(frame) if self.is_file else frame), time.monotonic()
                self._id += 1
                self._cond.notify_all()

    def read(self, last_id=0, timeout=3.0):
        """last_id より新しいフレームを待ち、(番号, フレーム, 取得時刻)を返す。停止・タイムアウトなら frame=None。"""
        self._go.set()
        with self._cond:
            self._cond.wait_for(lambda: self._id > last_id or self._failed, timeout)
            if self._id <= last_id:
                return last_id, None, 0.0
            return self._id, self._frame, self._time

    def describe(self):
        w, h = int(self._cap.get(cv2.CAP_PROP_FRAME_WIDTH)), int(self._cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
        if self.is_file:
            sh, sw = self._shrink(np.zeros((h, w), np.uint8)).shape[:2]
            return f"video file {w}x{h} (processed at {sw}x{sh}) @ {self._fps:.0f}fps, played at real speed"
        return f"{w}x{h} @ {self._cap.get(cv2.CAP_PROP_FPS):.0f}fps"

    def stop(self):
        self._stop.set()
        self._thread.join(timeout=1.0)
        self._cap.release()


def parse_args():
    parser = argparse.ArgumentParser(description="Realtime punch detection from a camera.")
    parser.add_argument("camera", nargs="?", default="0",
                        help="Camera index (default: 0), or a video file to use instead of the camera "
                             "(played at real speed, like a camera)")
    parser.add_argument("--width", type=int, default=640,
                        help="Capture width (default: 640). For a video file: shrink to fit this width")
    parser.add_argument("--height", type=int, default=480,
                        help="Capture height (default: 480). For a video file: shrink to fit this height")
    parser.add_argument("--fps", type=int, default=60, help="Requested capture fps (default: 60)")
    parser.add_argument("--no-mjpg", action="store_true", help="Do not request MJPG (use if the camera misbehaves)")
    parser.add_argument("--no-display", action="store_true", help="Do not show the video window (faster; stop with Ctrl+C)")
    parser.add_argument("--windowed", action="store_true", help="Show in a normal window instead of full screen (F toggles)")
    parser.add_argument("--stats", action="store_true", help="Print fps and processing time every 2 seconds")
    parser.add_argument("--calib-key", default="c", help="Key to hold for the head / body reference (default: c)")
    parser.add_argument("--hold-timeout", type=float, default=0.7,
                        help="The key counts as released this many seconds after its last key event (default: 0.7)")
    add_pipeline_args(parser)
    args = check_args(parser, parser.parse_args())
    if args.camera.isdigit():
        args.camera = int(args.camera)
    elif not Path(args.camera).exists():
        parser.error(f"camera must be a number (0, 1, ...) or an existing video file: {args.camera}")
    return args


def main():
    args = parse_args()
    display = not args.no_display
    camera = ThreadedCamera(args.camera, args.width, args.height, args.fps, not args.no_mjpg).start()
    pipeline = reporter = None
    try:
        print(f"Camera started: {camera.describe()}")
        # 表示があるときはキーを押している間、ないときは起動直後の数フレームで、頭・体の基準を測る
        pipeline = Pipeline(args, None if display else args.motion_calib_frames)
        reporter = Reporter(args.fmt, args.quiet, args.print_every, args.udp, args.udp_full)
        screen = Screen(WINDOW, fullscreen=not args.windowed, size=(args.width, args.height)) if display else None
        hold = HoldKey(args.calib_key, args.hold_timeout) if display else None
        view = ScreenView(args) if display else None
        was_held = False
        print("Running. Keep the bag still for the first moments (calibration).")
        if hold:
            print(f"Hold '{args.calib_key.upper()}' in your guard stance (bag still) to set the reference; "
                  f"you can hold it again at any time to measure it again.")

        last_id = index = frames = 0
        busy, report_start = 0.0, time.perf_counter()
        while True:
            last_id, frame, stamp = camera.read(last_id)
            if frame is None:
                print("The video ended. Stopping." if camera.is_file else "No frame received from the camera. Stopping.")
                break
            start = time.perf_counter()
            if hold:  # キーを押した瞬間に測り始め、離した瞬間に、その間の値を新しい基準にする(いつでも何度でも)
                held = hold.held(stamp)
                if held != was_held:
                    pipeline.set_calibrating(held)
                    was_held = held
            result = pipeline.process(frame, index, stamp)
            busy += time.perf_counter() - start
            frames += 1
            index += 1
            reporter.emit(result)

            if display:
                frame = view.draw(frame, pipeline, result, hint=f"Hold '{args.calib_key.upper()}' to (re)calibrate")
                cv2.imshow(WINDOW, frame)
                draw_overlay(frame, pipeline.bag, pipeline.person, pipeline.judge, pipeline.motion, result,
                             hint=f"Hold '{args.calib_key.upper()}' to (re)calibrate")
                screen.show(frame)
                key = cv2.waitKey(1)
                view.key(key)
                if hold:
                    hold.feed(key, stamp)
                if screen.handle_key(key):
                    break
            if args.stats and time.perf_counter() - report_start >= 2.0:
                elapsed = time.perf_counter() - report_start
                print(f"[stats] {frames / elapsed:.1f} fps, {busy / frames * 1000:.1f} ms/frame ({pipeline.device.label})")
                frames, busy, report_start = 0, 0.0, time.perf_counter()
    except KeyboardInterrupt:
        pass
    finally:
        camera.stop()
        if reporter:
            reporter.close()
        if pipeline:
            pipeline.close()
        cv2.destroyAllWindows()
        print("Stopped.")


if __name__ == "__main__":
    main()
