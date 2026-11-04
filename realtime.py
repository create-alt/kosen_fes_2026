"""リアルタイム用: カメラ映像でパンチ検出・ヒット判定・頭と体の動きを出力する。

    python realtime.py                       # カメラ0。GPU があれば自動で使う
    python realtime.py --device cpu          # CPU で実行
    python realtime.py --udp 127.0.0.1:5005  # 毎フレーム Unity へ UDP で送る
    python realtime.py --no-display --quiet --udp 127.0.0.1:5005   # Unity の裏で動かす(画面・出力なし)
    python realtime.py --stats               # 2秒ごとに fps と処理時間を表示

毎フレーム「頭の座標・体の座標・ヒット有無・パンチ有無」を出力する(--format json で JSON)。
起動直後の約 --calib-frames フレームは、バッグを静止させておく(基準の傾きを測るため)。
頭・体の基準位置は、**構えた姿勢で C キーを押している間**に測る(離すと確定。何度でもやり直せる)。
--no-display のときはキーが使えないので、起動直後の --motion-calib-frames フレームで自動で測る。
"""

import argparse
import threading
import time

import cv2

from args import add_pipeline_args, check_args
from logic import HoldKey
from output import Reporter, draw_overlay
from pipeline import Pipeline

WINDOW = "Punching Bag (realtime) - press Q to quit"


class ThreadedCamera:
    """別スレッドで常に最新のフレームだけを読み続ける(処理が遅れても遅延が溜まらない)。"""

    def __init__(self, index=0, width=640, height=480, fps=60, mjpg=True):
        self._cap = cv2.VideoCapture(index)
        if not self._cap.isOpened():
            raise SystemExit(f"Could not open camera: {index}")
        if mjpg:  # MJPG にすると USB カメラで高いフレームレートが出やすい
            self._cap.set(cv2.CAP_PROP_FOURCC, cv2.VideoWriter_fourcc(*"MJPG"))
        for prop, value in ((cv2.CAP_PROP_FRAME_WIDTH, width), (cv2.CAP_PROP_FRAME_HEIGHT, height),
                            (cv2.CAP_PROP_FPS, fps), (cv2.CAP_PROP_BUFFERSIZE, 1)):
            if value:
                self._cap.set(prop, value)
        self._cond = threading.Condition()
        self._frame, self._id, self._time, self._failed = None, 0, 0.0, False
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._loop, daemon=True)

    def start(self):
        self._thread.start()
        return self

    def _loop(self):
        while not self._stop.is_set():
            ok, frame = self._cap.read()
            with self._cond:
                if not ok:
                    self._failed = True
                    self._cond.notify_all()
                    return
                self._frame, self._time = frame, time.monotonic()
                self._id += 1
                self._cond.notify_all()

    def read(self, last_id=0, timeout=3.0):
        """last_id より新しいフレームを待ち、(番号, フレーム, 取得時刻)を返す。停止・タイムアウトなら frame=None。"""
        with self._cond:
            self._cond.wait_for(lambda: self._id > last_id or self._failed, timeout)
            if self._id <= last_id:
                return last_id, None, 0.0
            return self._id, self._frame, self._time

    def describe(self):
        w, h = int(self._cap.get(cv2.CAP_PROP_FRAME_WIDTH)), int(self._cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
        return f"{w}x{h} @ {self._cap.get(cv2.CAP_PROP_FPS):.0f}fps"

    def stop(self):
        self._stop.set()
        self._thread.join(timeout=1.0)
        self._cap.release()


def parse_args():
    parser = argparse.ArgumentParser(description="Realtime punch detection from a camera.")
    parser.add_argument("camera", nargs="?", type=int, default=0, help="Camera index (default: 0)")
    parser.add_argument("--width", type=int, default=640, help="Capture width (default: 640)")
    parser.add_argument("--height", type=int, default=480, help="Capture height (default: 480)")
    parser.add_argument("--fps", type=int, default=60, help="Requested capture fps (default: 60)")
    parser.add_argument("--no-mjpg", action="store_true", help="Do not request MJPG (use if the camera misbehaves)")
    parser.add_argument("--no-display", action="store_true", help="Do not show the video window (faster; stop with Ctrl+C)")
    parser.add_argument("--stats", action="store_true", help="Print fps and processing time every 2 seconds")
    parser.add_argument("--calib-key", default="c", help="Key to hold for the head / body reference (default: c)")
    parser.add_argument("--hold-timeout", type=float, default=0.7,
                        help="The key counts as released this many seconds after its last key event (default: 0.7)")
    add_pipeline_args(parser)
    return check_args(parser, parser.parse_args())


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
        hold = HoldKey(args.calib_key, args.hold_timeout) if display and pipeline.motion else None
        print("Running. Keep the bag still for the first moments (calibration).")
        if hold:
            print(f"Hold '{args.calib_key.upper()}' in your guard stance to set the head / body reference.")

        last_id = index = frames = 0
        busy, report_start = 0.0, time.perf_counter()
        while True:
            last_id, frame, stamp = camera.read(last_id)
            if frame is None:
                print("No frame received from the camera. Stopping.")
                break
            start = time.perf_counter()
            if hold:
                pipeline.motion.set_calibrating(hold.held(stamp))
            result = pipeline.process(frame, index, stamp)
            busy += time.perf_counter() - start
            frames += 1
            index += 1
            reporter.emit(result)

            if display:
                draw_overlay(frame, pipeline.bag, pipeline.person, pipeline.judge, pipeline.motion, result,
                             hint=f"Hold '{args.calib_key.upper()}' in your guard stance")
                cv2.imshow(WINDOW, frame)
                key = cv2.waitKey(1)
                if hold:
                    hold.feed(key, stamp)
                if key & 0xFF == ord("q"):
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
