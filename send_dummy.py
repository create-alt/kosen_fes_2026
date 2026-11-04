"""Unity への送信テスト用: カメラも YOLO も使わず、ダミーの値を本番と同じ形式で UDP 送信する。

    python unity_test/send_dummy.py --udp 192.168.1.20:5005         # Unity を動かす PC の IP を指定
    python unity_test/send_dummy.py --udp 127.0.0.1:5005 --seconds 5   # 同じ PC 内での確認

送る JSON は main.py / realtime.py の --udp と全く同じ(output.FrameResult → Reporter)。
送る内容は {"forward", "head", "feet", "punch"}。forward は 0.1〜0.9 を往復、head は 0.6〜0.8 で上下、
feet は 0.0 固定、punch は 1秒ごとに 1 フレームだけ 1 になる。
"""

import argparse
import math
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from output import FrameResult, Reporter  # noqa: E402


def main():
    p = argparse.ArgumentParser(description="Send dummy per-frame data to Unity over UDP.")
    p.add_argument("--udp", required=True, metavar="HOST:PORT", help="Unity 側の IP:ポート(例 192.168.1.20:5005)")
    p.add_argument("--fps", type=float, default=30.0)
    p.add_argument("--seconds", type=float, default=0.0, help="この秒数で終了(0 なら Ctrl+C まで)")
    p.add_argument("--print-every", type=int, default=30, help="コンソールには N フレームに 1 回出す")
    args = p.parse_args()

    reporter = Reporter("text", quiet=False, print_every=args.print_every, udp=args.udp)
    start = time.perf_counter()
    index = punches = 0
    was_hit = False
    try:
        while args.seconds <= 0 or time.perf_counter() - start < args.seconds:
            t = time.perf_counter() - start
            hit = (t % 1.0) < 0.2
            punch = hit and not was_hit                # 1秒ごとに 1 フレームだけパンチ
            was_hit = hit
            punches += punch
            reporter.emit(FrameResult(
                frame=index, time=t, size=(960, 540), punch=punch, punches=punches,
                game_forward=0.5 + 0.4 * math.sin(t),       # 体: 0.1〜0.9 を往復
                game_head=0.7 + 0.1 * math.sin(2 * t)))     # 頭: 0.6〜0.8
            index += 1
            time.sleep(max(0.0, start + index / args.fps - time.perf_counter()))
    except KeyboardInterrupt:
        pass
    finally:
        reporter.close()
        print(f"Sent {index} packets.")


if __name__ == "__main__":
    main()
